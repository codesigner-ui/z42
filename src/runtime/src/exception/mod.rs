// Exception runtime — z42 exception object layout and propagation model.
//
// Phase 1 exception machinery (PENDING_EXCEPTION slot, UserException sentinel)
// lives in interp/mod.rs; this module will centralise the exception type
// hierarchy and cross-backend unwinding for Phase 2+.
