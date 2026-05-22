//! `Region<T>` —— chunked region allocator backing for GC entries.
//!
//! **add-custom-allocator P0 (2026-05-22)**: replaces the per-object
//! `Arc<GcAllocation<T>>` storage. Each `Region<T>` owns
//! `Vec<Box<[MaybeUninit<RegionEntry<T>>; CHUNK_SIZE]>>` — chunks are
//! Box-owned and never relocate, so `RegionEntry` addresses remain
//! stable for `GcRef::as_ptr` (identity hashing) until the entry is
//! tombstoned by sweep.
//!
//! # Allocation model
//!
//! - **Fast path**: free list pop — reuses a tombstoned slot from a
//!   prior sweep cycle. Generation counter incremented at tombstone
//!   time prevents stale `WeakGcRef` from upgrading to the new
//!   occupant (ABA prevention).
//! - **Slow path**: bump pointer within the current chunk. When the
//!   chunk fills, grow `chunks` and start fresh.
//!
//! # Sweep model (P1+ wiring)
//!
//! `iterate_alive(visit)` walks all chunks linearly, skipping
//! tombstoned (alive=false) entries. `tombstone(handle)` flips alive
//! to false, bumps generation, pushes the slot to free_list. No
//! `Drop` runs on the data — finalizer dispatch is the caller's
//! responsibility (`sweep_phase` in `ArcMagrGC` per spec D3).
//!
//! # Concurrency
//!
//! The region itself is **not** `Sync`; callers (`ArcMagrGC`) wrap
//! it in `parking_lot::Mutex<Region<T>>` for the alloc / tombstone
//! paths. `RegionEntry` data access goes through its own
//! `parking_lot::Mutex<T>` for fine-grained locking (preserves
//! `add-multithreading-foundation` concurrency model per design D6).

use std::marker::PhantomData;
use std::mem::MaybeUninit;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU8, Ordering};

use parking_lot::Mutex;

use super::types::FinalizerFn;

/// Chunk capacity (entries per chunk). 256 balances:
/// - Per-chunk allocation cost (1 malloc per CHUNK_SIZE allocs amortizes)
/// - Cache locality for sweep traversal (chunk fits in ~16-64 KB depending on T)
/// - Granularity for future per-thread arenas (256 is a reasonable batch)
pub(crate) const CHUNK_SIZE: usize = 256;

/// Per-object slot inside a `Region<T>`. Holds the user data plus GC
/// metadata. Address stability: once a `RegionEntry` is initialized
/// inside a chunk, its `&self` reference remains valid until the
/// owning chunk's Box is dropped (which happens only when the Region
/// itself drops — never during normal sweep cycles).
// P0 lands this scaffolding; P1 wires up the `value` + `finalizer`
// fields via the rewritten `GcRef` / `WeakGcRef` and the relocated
// `set_finalizer` path. `allow(dead_code)` silences the unused-field
// warnings until then (VM tests treat any stderr warning as a regression).
#[allow(dead_code)]
pub struct RegionEntry<T> {
    /// User value. `Mutex` provides per-entry locking (preserves the
    /// multi-threading concurrency model). Access via
    /// `entry.value.lock()` from `GcRef::borrow` / `borrow_mut`.
    pub(crate) value: Mutex<T>,

    /// Mark bit (add-mark-sweep-collector + add-concurrent-gc). CAS
    /// from 0 to 1 by mark phase / write barrier. Sweep resets to 0
    /// on survivors. `Relaxed` ordering — visibility sync via the
    /// gc_phase Mutex / mark_queue Mutex established at sweep / drain
    /// boundaries.
    pub(crate) marked: AtomicU8,

    /// Tombstone flag. `true` while the slot holds a live user
    /// object; `false` after sweep reclaims it. `Acquire / Release`
    /// ordering pairs with `WeakGcRef::upgrade` reads + sweep writes
    /// (prevents reading half-tombstoned state).
    pub(crate) alive: AtomicBool,

    /// Generation counter. Bumped on every tombstone. `GcRef` and
    /// `WeakGcRef` both record the generation at construction; access
    /// methods (`upgrade`, `borrow`) check the recorded generation
    /// matches the entry's current generation. Mismatch → entry was
    /// reclaimed + slot reused → return None / panic (per design D5).
    pub(crate) generation: AtomicU32,

    /// One-shot finalizer slot. `Mutex<Option<FinalizerFn>>` so the
    /// sweep path can `take()` the closure atomically (fire-once
    /// semantics — matches add-mark-sweep-collector behavior).
    pub(crate) finalizer: Mutex<Option<FinalizerFn>>,
}

impl<T> RegionEntry<T> {
    fn new(value: T) -> Self {
        Self {
            value:      Mutex::new(value),
            marked:     AtomicU8::new(0),
            alive:      AtomicBool::new(true),
            generation: AtomicU32::new(0),
            finalizer:  Mutex::new(None),
        }
    }

    /// Atomically attempt to mark this entry (0 → 1). Returns `true`
    /// if this call won the CAS (first to mark in the current cycle).
    /// Used by mark phase BFS + concurrent barrier override.
    pub fn mark(&self) -> bool {
        self.marked
            .compare_exchange(0, 1, Ordering::Relaxed, Ordering::Relaxed)
            .is_ok()
    }

    /// Read current mark state. Used by sweep to decide retention.
    pub fn is_marked(&self) -> bool {
        self.marked.load(Ordering::Relaxed) != 0
    }

    /// Reset mark to 0. Used by sweep on survivors to prep next cycle.
    pub fn clear_mark(&self) {
        self.marked.store(0, Ordering::Relaxed);
    }
}

/// Opaque handle into a `Region<T>`. Encodes (chunk index, entry
/// index within chunk, generation snapshot). 12 bytes total —
/// `Copy`-able primitive components but the public `GcRef<T>` wrapper
/// in `refs.rs` enforces `Clone`-only (per design D9).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RegionHandle {
    pub(crate) chunk_idx: u16,
    pub(crate) entry_idx: u16,
    pub(crate) generation: u32,
}

/// Chunked region allocator. Owns user objects of type `T` plus
/// per-object GC metadata. See module-level docs for the allocation
/// + sweep model.
pub struct Region<T> {
    /// Chunks of pre-reserved entries. Each chunk is a fixed-size
    /// `Box<[MaybeUninit<RegionEntry<T>>; CHUNK_SIZE]>` so its
    /// address is stable for the chunk's lifetime.
    chunks: Vec<Box<[MaybeUninit<RegionEntry<T>>; CHUNK_SIZE]>>,

    /// (chunk_idx, entry_idx) — next bump-pointer position. Advances
    /// linearly; never goes back. Free list pops are separate.
    next_bump: (u16, u16),

    /// Tombstoned slots reusable by fresh allocs. LIFO (Vec::pop).
    free_list: Vec<(u16, u16)>,

    /// Track initialized vs uninitialized slots. Bit `(ci, ei)` is
    /// set if the slot is initialized (was alloc'd at least once).
    /// Sweep uses this to skip never-allocated slots in the last
    /// chunk (where bump hasn't reached the end).
    ///
    /// One bool per slot — could compress to bitmap later; v1
    /// favors clarity.
    initialized: Vec<Vec<bool>>,

    _phantom: PhantomData<T>,
}

impl<T> Default for Region<T> {
    fn default() -> Self {
        Self {
            chunks:      Vec::new(),
            next_bump:   (0, 0),
            free_list:   Vec::new(),
            initialized: Vec::new(),
            _phantom:    PhantomData,
        }
    }
}

impl<T> Region<T> {
    pub fn new() -> Self {
        Self::default()
    }

    /// Allocate `value` into the region. Returns a stable handle.
    ///
    /// Fast path: pop a tombstoned slot from `free_list`. The slot
    /// already has initialized memory; we drop the old (dead)
    /// `RegionEntry` and write a fresh one. The generation was
    /// bumped at tombstone time so the new handle's generation is
    /// the current entry generation.
    ///
    /// Slow path: bump pointer. If the current chunk is full, push
    /// a new chunk first.
    pub fn alloc(&mut self, value: T) -> RegionHandle {
        if let Some((ci, ei)) = self.free_list.pop() {
            // Slot is initialized (we tombstoned it previously). Drop
            // the dead RegionEntry, write a fresh one preserving the
            // bumped generation.
            let chunk = &mut self.chunks[ci as usize];
            // SAFETY: slot was init at first alloc; we're reading the
            // current RegionEntry to extract its generation, then
            // overwriting in place. Dropping a `RegionEntry<T>` runs
            // its Mutex / AtomicU8 / etc. Drop impls — all safe.
            let slot = unsafe { chunk[ei as usize].assume_init_mut() };
            let gen = slot.generation.load(Ordering::Acquire);
            // Replace the entry in place. Drop the old, write new.
            let new_entry = RegionEntry::new(value);
            // Manually preserve the generation across the replacement.
            new_entry.generation.store(gen, Ordering::Release);
            // SAFETY: ptr-write replaces the old entry with new.
            // The old's Drop runs as part of the assignment.
            *slot = new_entry;
            return RegionHandle { chunk_idx: ci, entry_idx: ei, generation: gen };
        }

        // Bump pointer.
        let (ci, ei) = self.next_bump;
        if (ci as usize) >= self.chunks.len() {
            // Grow: push a new chunk of MaybeUninit entries.
            // SAFETY: MaybeUninit<RegionEntry<T>> is valid to leave uninit.
            let chunk: Box<[MaybeUninit<RegionEntry<T>>; CHUNK_SIZE]> = Box::new(unsafe {
                MaybeUninit::<[MaybeUninit<RegionEntry<T>>; CHUNK_SIZE]>::uninit().assume_init()
            });
            self.chunks.push(chunk);
            self.initialized.push(vec![false; CHUNK_SIZE]);
        }
        let chunk = &mut self.chunks[ci as usize];
        chunk[ei as usize] = MaybeUninit::new(RegionEntry::new(value));
        self.initialized[ci as usize][ei as usize] = true;

        // Advance next_bump.
        let next_ei = ei + 1;
        if (next_ei as usize) >= CHUNK_SIZE {
            self.next_bump = (ci + 1, 0);
        } else {
            self.next_bump = (ci, next_ei);
        }

        RegionHandle { chunk_idx: ci, entry_idx: ei, generation: 0 }
    }

    /// Resolve a handle to a `&RegionEntry<T>` reference. Panics if
    /// the handle's chunk/entry is out of bounds (programmer error;
    /// should never happen with valid `GcRef`).
    ///
    /// Does NOT check generation or alive — that's the caller's job
    /// (different paths want different responses: `WeakGcRef::upgrade`
    /// returns None, `GcRef::borrow` panics).
    pub fn resolve(&self, handle: RegionHandle) -> &RegionEntry<T> {
        let chunk = &self.chunks[handle.chunk_idx as usize];
        let slot = &chunk[handle.entry_idx as usize];
        // SAFETY: the handle was constructed via `alloc`, which sets
        // initialized[ci][ei] = true. As long as the handle came
        // from this Region (typestate), the slot is init.
        unsafe { slot.assume_init_ref() }
    }

    /// Tombstone the entry pointed to by `handle`. Sets `alive=false`,
    /// bumps generation, pushes slot to free_list. Does NOT call the
    /// finalizer — that's the caller's responsibility (sweep extracts
    /// + invokes the finalizer separately).
    ///
    /// Returns `false` if the handle's generation no longer matches
    /// (slot was already tombstoned + reused — stale handle). In that
    /// case the call is a no-op.
    pub fn tombstone(&mut self, handle: RegionHandle) -> bool {
        let entry = self.resolve(handle);
        if entry.generation.load(Ordering::Acquire) != handle.generation {
            return false;
        }
        if !entry.alive.load(Ordering::Acquire) {
            return false;
        }
        entry.alive.store(false, Ordering::Release);
        entry.generation.fetch_add(1, Ordering::AcqRel);
        self.free_list.push((handle.chunk_idx, handle.entry_idx));
        true
    }

    /// Iterate every currently-alive entry. Skips uninit slots in
    /// the last chunk (bump hasn't reached the end) and tombstoned
    /// slots. Order: chunk 0 → chunk N, entry 0 → CHUNK_SIZE-1 within.
    pub fn iterate_alive(&self, mut visit: impl FnMut(RegionHandle, &RegionEntry<T>)) {
        for (ci, chunk) in self.chunks.iter().enumerate() {
            for ei in 0..CHUNK_SIZE {
                if !self.initialized[ci][ei] {
                    continue;
                }
                let slot = &chunk[ei];
                let entry = unsafe { slot.assume_init_ref() };
                if !entry.alive.load(Ordering::Acquire) {
                    continue;
                }
                let h = RegionHandle {
                    chunk_idx:  ci as u16,
                    entry_idx:  ei as u16,
                    generation: entry.generation.load(Ordering::Acquire),
                };
                visit(h, entry);
            }
        }
    }

    /// Number of alive entries (linear scan). Mostly for tests +
    /// diagnostics; production uses stats counters.
    pub fn alive_count(&self) -> usize {
        let mut n = 0;
        self.iterate_alive(|_, _| n += 1);
        n
    }

    /// Total slot capacity across all chunks. `alive_count <= total <=
    /// chunks.len() * CHUNK_SIZE`.
    #[allow(dead_code)]
    pub(crate) fn total_capacity(&self) -> usize {
        self.chunks.len() * CHUNK_SIZE
    }

    /// Number of free slots available without growing (`free_list +
    /// remaining bump capacity in current chunk`). Used by P3 bench
    /// + diagnostics.
    #[allow(dead_code)]
    pub(crate) fn free_slot_count(&self) -> usize {
        let bump_remaining = if (self.next_bump.0 as usize) >= self.chunks.len() {
            0
        } else {
            CHUNK_SIZE - self.next_bump.1 as usize
        };
        self.free_list.len() + bump_remaining
    }
}

impl<T> Drop for Region<T> {
    /// Drop every initialized entry. Each entry's own Drop impl
    /// handles its Mutex + Atomic / etc. The `value: Mutex<T>` Drop
    /// runs `T::drop` for the user data — at this point the Region
    /// is being torn down (heap shutdown), so prompt user-data drop
    /// is appropriate.
    fn drop(&mut self) {
        for (ci, chunk) in self.chunks.iter_mut().enumerate() {
            for ei in 0..CHUNK_SIZE {
                if !self.initialized[ci][ei] {
                    continue;
                }
                // SAFETY: initialized slot. Drop in place.
                unsafe { chunk[ei].assume_init_drop(); }
            }
        }
    }
}

#[cfg(test)]
#[path = "region_tests.rs"]
mod region_tests;
