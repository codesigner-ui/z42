//! `Std.Net.Sockets` builtins — sync blocking TCP sockets.
//!
//! add-z42-net (K1, 2026-05-24): pattern mirrors `process.rs` (slot-id
//! handle + per-builtin slot lookup). All cross-platform differences
//! (BSD vs Winsock vs iOS/Android) are delegated to Rust
//! `std::net::{TcpStream, TcpListener}`.
//!
//! ## Return shape
//!
//! All builtins (except `*_drop`, which return `Value::Null`) return a
//! discriminated `Value::Array` tuple. The first element is always a
//! `KIND_*` tag so z42 decoding is uniform:
//!
//! ```text
//!   [I64(0), I64(slot)]                       // KIND_OK — connect / accept
//!   [I64(0), I64(slot), I64(actual_port)]     // KIND_OK — listen
//!   [I64(0), I64(nbytes)]                     // KIND_OK — read / write (0 = EOF)
//!   [I64(1), Str(message)]                    // KIND_SOCKET_ERR — io fail
//!   [I64(2)]                                  // KIND_HANDLE_INVALID — slot missing
//!   [I64(3)]                                  // KIND_UNSUPPORTED — wasm32
//! ```
//!
//! Drops always return `Value::Null` (idempotent; no error path needed).

use super::convert::{arg_i64, arg_str};
use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

pub(crate) const KIND_OK:              i64 = 0;
pub(crate) const KIND_SOCKET_ERR:      i64 = 1;
pub(crate) const KIND_HANDLE_INVALID:  i64 = 2;
#[cfg(target_arch = "wasm32")]
pub(crate) const KIND_UNSUPPORTED:     i64 = 3;

fn ok_value(ctx: &VmContext, v: i64) -> Value {
    ctx.heap().alloc_array(vec![Value::I64(KIND_OK), Value::I64(v)])
}

fn ok_two(ctx: &VmContext, a: i64, b: i64) -> Value {
    ctx.heap().alloc_array(vec![Value::I64(KIND_OK), Value::I64(a), Value::I64(b)])
}

fn socket_err(ctx: &VmContext, msg: String) -> Value {
    ctx.heap().alloc_array(vec![Value::I64(KIND_SOCKET_ERR), Value::Str(msg.into())])
}

fn handle_invalid(ctx: &VmContext) -> Value {
    ctx.heap().alloc_array(vec![Value::I64(KIND_HANDLE_INVALID)])
}

#[cfg(target_arch = "wasm32")]
fn unsupported(ctx: &VmContext) -> Value {
    ctx.heap().alloc_array(vec![Value::I64(KIND_UNSUPPORTED)])
}

fn require_slot_id(args: &[Value], idx: usize, name: &str) -> Result<u64> {
    let n = arg_i64(args, idx, name)?;
    if n < 0 {
        bail!("{}: slot id must be non-negative, got {}", name, n);
    }
    Ok(n as u64)
}

fn require_port(args: &[Value], idx: usize, name: &str) -> Result<u16> {
    let p = arg_i64(args, idx, name)?;
    if !(0..=65535).contains(&p) {
        bail!("{}: port out of range [0, 65535]: {}", name, p);
    }
    Ok(p as u16)
}

// ── desktop / mobile (non-wasm32) implementations ─────────────────────────

#[cfg(not(target_arch = "wasm32"))]
mod imp {
    use super::*;
    use std::net::{TcpListener, TcpStream, SocketAddr, ToSocketAddrs};
    use std::io::{Read, Write};

    pub fn builtin_net_tcp_connect(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_connect";
        let host = arg_str(args, 0, NAME)?.to_string();
        let port = require_port(args, 1, NAME)?;

        let addr = format!("{}:{}", host, port);
        match TcpStream::connect(&addr) {
            Ok(stream) => {
                let slot_id = ctx.alloc_tcp_socket_slot(stream);
                Ok(ok_value(ctx, slot_id as i64))
            }
            Err(e) => Ok(socket_err(ctx, format!("connect to {}: {}", addr, e))),
        }
    }

    pub fn builtin_net_tcp_listen(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_listen";
        let host = arg_str(args, 0, NAME)?.to_string();
        let port = require_port(args, 1, NAME)?;

        let bind_target = format!("{}:{}", host, port);
        let bind_result = bind_target.to_socket_addrs()
            .and_then(|mut iter| iter.next()
                .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::AddrNotAvailable, "no addresses")))
            .and_then(|addr: SocketAddr| TcpListener::bind(addr));

        match bind_result {
            Ok(listener) => {
                let actual_port = listener.local_addr()
                    .map(|a| a.port())
                    .unwrap_or(port);
                let slot_id = ctx.alloc_tcp_listener_slot(listener);
                Ok(ok_two(ctx, slot_id as i64, actual_port as i64))
            }
            Err(e) => Ok(socket_err(ctx, format!("bind {}: {}", bind_target, e))),
        }
    }

    pub fn builtin_net_tcp_accept(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_accept";
        let slot_id = require_slot_id(args, 0, NAME)?;

        // Take the listener out so `.accept()` can block without holding
        // the global listener table lock.
        let listener = {
            let mut map = ctx.core.tcp_listeners.lock();
            map.remove(&slot_id)
        };
        let Some(listener) = listener else {
            return Ok(handle_invalid(ctx));
        };

        let accept_result = listener.accept();
        // Put listener back so subsequent Accept calls work.
        ctx.core.tcp_listeners.lock().insert(slot_id, listener);

        match accept_result {
            Ok((stream, _peer)) => {
                let sock_slot = ctx.alloc_tcp_socket_slot(stream);
                Ok(ok_value(ctx, sock_slot as i64))
            }
            Err(e) => Ok(socket_err(ctx, format!("accept: {}", e))),
        }
    }

    pub fn builtin_net_tcp_socket_read(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_socket_read";
        let slot_id = require_slot_id(args, 0, NAME)?;
        let buf_arr = match args.get(1) {
            Some(Value::Array(rc)) => rc.clone(),
            other => bail!("{}: arg 1 expected byte array, got {:?}", NAME, other),
        };
        let offset = arg_i64(args, 2, NAME)? as usize;
        let count  = arg_i64(args, 3, NAME)? as usize;

        let buf_len = buf_arr.borrow().len();
        if offset + count > buf_len {
            bail!("{}: offset {} + count {} exceeds buf length {}", NAME, offset, count, buf_len);
        }
        if count == 0 { return Ok(ok_value(ctx, 0)); }

        let stream = {
            let mut map = ctx.core.tcp_sockets.lock();
            map.remove(&slot_id)
        };
        let Some(mut stream) = stream else {
            return Ok(handle_invalid(ctx));
        };

        let mut tmp = vec![0u8; count];
        let read_result = stream.read(&mut tmp);

        ctx.core.tcp_sockets.lock().insert(slot_id, stream);

        match read_result {
            Ok(n) => {
                let mut borrowed = buf_arr.borrow_mut();
                for i in 0..n {
                    borrowed[offset + i] = Value::I64(tmp[i] as i64);
                }
                Ok(ok_value(ctx, n as i64))
            }
            Err(e) => Ok(socket_err(ctx, format!("read: {}", e))),
        }
    }

    pub fn builtin_net_tcp_socket_write(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_socket_write";
        let slot_id = require_slot_id(args, 0, NAME)?;
        let buf_arr = match args.get(1) {
            Some(Value::Array(rc)) => rc.clone(),
            other => bail!("{}: arg 1 expected byte array, got {:?}", NAME, other),
        };
        let offset = arg_i64(args, 2, NAME)? as usize;
        let count  = arg_i64(args, 3, NAME)? as usize;

        let buf_len = buf_arr.borrow().len();
        if offset + count > buf_len {
            bail!("{}: offset {} + count {} exceeds buf length {}", NAME, offset, count, buf_len);
        }
        if count == 0 { return Ok(ok_value(ctx, 0)); }

        let mut tmp = vec![0u8; count];
        {
            let borrowed = buf_arr.borrow();
            for i in 0..count {
                match &borrowed[offset + i] {
                    Value::I64(v) => tmp[i] = (*v as i64 & 0xFF) as u8,
                    other => bail!("{}: byte[] elem at {} expected I64, got {:?}", NAME, offset + i, other),
                }
            }
        }

        let stream = {
            let mut map = ctx.core.tcp_sockets.lock();
            map.remove(&slot_id)
        };
        let Some(mut stream) = stream else {
            return Ok(handle_invalid(ctx));
        };

        let write_result = stream.write_all(&tmp).map(|_| count);

        ctx.core.tcp_sockets.lock().insert(slot_id, stream);

        match write_result {
            Ok(n) => Ok(ok_value(ctx, n as i64)),
            Err(e) => Ok(socket_err(ctx, format!("write: {}", e))),
        }
    }

    pub fn builtin_net_tcp_socket_drop(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_socket_drop";
        let slot_id = require_slot_id(args, 0, NAME)?;
        ctx.core.tcp_sockets.lock().remove(&slot_id);
        Ok(Value::Null)
    }

    // add-httpclient-timeout (2026-05-27): apply read / write deadlines so
    // a misbehaving peer can't hang the script. `millis <= 0` clears the
    // timeout (blocking I/O). On error returns socket_err; on missing slot
    // returns handle_invalid (caller treats as already-disposed).

    fn apply_timeout(
        ctx: &VmContext,
        slot_id: u64,
        millis: i64,
        which: &'static str,
    ) -> Result<Value> {
        let dur = if millis > 0 {
            Some(std::time::Duration::from_millis(millis as u64))
        } else {
            None
        };
        let stream = {
            let map = ctx.core.tcp_sockets.lock();
            match map.get(&slot_id) {
                Some(s) => s.try_clone(),
                None => return Ok(handle_invalid(ctx)),
            }
        };
        let stream = match stream {
            Ok(s) => s,
            Err(e) => return Ok(socket_err(ctx, format!("{}: try_clone: {}", which, e))),
        };
        let result = if which == "set_read_timeout" {
            stream.set_read_timeout(dur)
        } else {
            stream.set_write_timeout(dur)
        };
        match result {
            Ok(()) => Ok(ok_value(ctx, 0)),
            Err(e) => Ok(socket_err(ctx, format!("{}: {}", which, e))),
        }
    }

    pub fn builtin_net_tcp_socket_set_read_timeout(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_socket_set_read_timeout";
        let slot_id = require_slot_id(args, 0, NAME)?;
        let millis = arg_i64(args, 1, NAME)?;
        apply_timeout(ctx, slot_id, millis, "set_read_timeout")
    }

    pub fn builtin_net_tcp_socket_set_write_timeout(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_socket_set_write_timeout";
        let slot_id = require_slot_id(args, 0, NAME)?;
        let millis = arg_i64(args, 1, NAME)?;
        apply_timeout(ctx, slot_id, millis, "set_write_timeout")
    }

    pub fn builtin_net_tcp_listener_drop(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_tcp_listener_drop";
        let slot_id = require_slot_id(args, 0, NAME)?;
        ctx.core.tcp_listeners.lock().remove(&slot_id);
        Ok(Value::Null)
    }

    // ── UDP builtins (add-z42-net-udp K2, 2026-05-25) ────────────────────
    use std::net::UdpSocket;

    /// `__net_udp_bind(host, port) -> [0, slot, actual_port] | err`
    pub fn builtin_net_udp_bind(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_udp_bind";
        let host = arg_str(args, 0, NAME)?.to_string();
        let port = require_port(args, 1, NAME)?;
        let bind_target = format!("{}:{}", host, port);
        let bind_result = bind_target.to_socket_addrs()
            .and_then(|mut iter| iter.next()
                .ok_or_else(|| std::io::Error::new(std::io::ErrorKind::AddrNotAvailable, "no addresses")))
            .and_then(|addr: SocketAddr| UdpSocket::bind(addr));
        match bind_result {
            Ok(sock) => {
                let actual_port = sock.local_addr().map(|a| a.port()).unwrap_or(port);
                let slot_id = ctx.alloc_udp_socket_slot(sock);
                Ok(ok_two(ctx, slot_id as i64, actual_port as i64))
            }
            Err(e) => Ok(socket_err(ctx, format!("udp bind {}: {}", bind_target, e))),
        }
    }

    /// `__net_udp_send(slot, buf, offset, count, host, port) -> [0, n] | err | invalid`
    pub fn builtin_net_udp_send(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_udp_send";
        let slot_id = require_slot_id(args, 0, NAME)?;
        let buf_arr = match args.get(1) {
            Some(Value::Array(rc)) => rc.clone(),
            other => bail!("{}: arg 1 expected byte array, got {:?}", NAME, other),
        };
        let offset = arg_i64(args, 2, NAME)? as usize;
        let count  = arg_i64(args, 3, NAME)? as usize;
        let host   = arg_str(args, 4, NAME)?.to_string();
        let port   = require_port(args, 5, NAME)?;

        let buf_len = buf_arr.borrow().len();
        if offset + count > buf_len {
            bail!("{}: offset {} + count {} exceeds buf length {}", NAME, offset, count, buf_len);
        }

        // Copy datagram bytes into a contiguous Vec<u8>.
        let mut tmp = vec![0u8; count];
        {
            let borrowed = buf_arr.borrow();
            for i in 0..count {
                match &borrowed[offset + i] {
                    Value::I64(v) => tmp[i] = (*v as i64 & 0xFF) as u8,
                    other => bail!("{}: byte[] elem at {} expected I64, got {:?}", NAME, offset + i, other),
                }
            }
        }

        // Take socket out for the blocking call; restore after.
        let sock_opt = {
            let mut map = ctx.core.udp_sockets.lock();
            map.remove(&slot_id)
        };
        let Some(sock) = sock_opt else {
            return Ok(handle_invalid(ctx));
        };

        let send_result = sock.send_to(&tmp, format!("{}:{}", host, port).as_str());
        ctx.core.udp_sockets.lock().insert(slot_id, sock);

        match send_result {
            Ok(n) => Ok(ok_value(ctx, n as i64)),
            Err(e) => Ok(socket_err(ctx, format!("udp send: {}", e))),
        }
    }

    /// `__net_udp_recv(slot) -> [0, byte[] buf, remote_host_str, remote_port_i64] | err | invalid`
    pub fn builtin_net_udp_recv(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_udp_recv";
        let slot_id = require_slot_id(args, 0, NAME)?;

        let sock_opt = {
            let mut map = ctx.core.udp_sockets.lock();
            map.remove(&slot_id)
        };
        let Some(sock) = sock_opt else {
            return Ok(handle_invalid(ctx));
        };

        // 65536 is large enough for any normal UDP datagram (incl IPv6 jumbo
        // up to 65507 payload + room for headers conceptually).
        let mut tmp = vec![0u8; 65536];
        let recv_result = sock.recv_from(&mut tmp);
        ctx.core.udp_sockets.lock().insert(slot_id, sock);

        match recv_result {
            Ok((n, peer)) => {
                // Build z42 byte[] sized to actual datagram length.
                let mut byte_vals: Vec<Value> = Vec::with_capacity(n);
                for i in 0..n {
                    byte_vals.push(Value::I64(tmp[i] as i64));
                }
                let buf_array = ctx.heap().alloc_array(byte_vals);
                let host_str = peer.ip().to_string();
                let port_i64 = peer.port() as i64;
                Ok(ctx.heap().alloc_array(vec![
                    Value::I64(KIND_OK),
                    buf_array,
                    Value::Str(host_str.into()),
                    Value::I64(port_i64),
                ]))
            }
            Err(e) => Ok(socket_err(ctx, format!("udp recv: {}", e))),
        }
    }

    /// `__net_udp_drop(slot) -> Null` — idempotent.
    pub fn builtin_net_udp_drop(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_udp_drop";
        let slot_id = require_slot_id(args, 0, NAME)?;
        ctx.core.udp_sockets.lock().remove(&slot_id);
        Ok(Value::Null)
    }

    /// add-z42-net-udp-recv-into (2026-05-27)
    /// `__net_udp_recv_into(slot, buf, offset, count) -> [0, n, host, port] | err | invalid`
    /// Receive a datagram directly into the caller's pre-allocated byte[].
    /// Avoids the per-call array allocation that plain `__net_udp_recv` does.
    /// Truncates the datagram silently if it exceeds `count` (matches BCL
    /// `UdpClient.Receive(byte[])` semantics — caller decides buffer size).
    pub fn builtin_net_udp_recv_into(ctx: &VmContext, args: &[Value]) -> Result<Value> {
        const NAME: &str = "__net_udp_recv_into";
        let slot_id = require_slot_id(args, 0, NAME)?;
        let buf_arr = match args.get(1) {
            Some(Value::Array(rc)) => rc.clone(),
            other => bail!("{}: arg 1 expected byte array, got {:?}", NAME, other),
        };
        let offset = arg_i64(args, 2, NAME)? as usize;
        let count  = arg_i64(args, 3, NAME)? as usize;

        let buf_len = buf_arr.borrow().len();
        if offset + count > buf_len {
            bail!("{}: offset {} + count {} exceeds buf length {}", NAME, offset, count, buf_len);
        }

        let sock_opt = {
            let mut map = ctx.core.udp_sockets.lock();
            map.remove(&slot_id)
        };
        let Some(sock) = sock_opt else {
            return Ok(handle_invalid(ctx));
        };

        // Recv into scratch buffer then copy into the caller's array — the
        // borrow_mut on the Rc-wrapped Vec<Value> doesn't escape so this
        // never aliases.
        let mut tmp = vec![0u8; count];
        let recv_result = sock.recv_from(&mut tmp);
        ctx.core.udp_sockets.lock().insert(slot_id, sock);

        match recv_result {
            Ok((n, peer)) => {
                // Copy received bytes (clamped to count, matching socket
                // truncation behaviour) into the caller's array.
                let mut borrowed = buf_arr.borrow_mut();
                let written = n.min(count);
                for i in 0..written {
                    borrowed[offset + i] = Value::I64(tmp[i] as i64);
                }
                let host_str = peer.ip().to_string();
                let port_i64 = peer.port() as i64;
                Ok(ctx.heap().alloc_array(vec![
                    Value::I64(KIND_OK),
                    Value::I64(written as i64),
                    Value::Str(host_str.into()),
                    Value::I64(port_i64),
                ]))
            }
            Err(e) => Ok(socket_err(ctx, format!("udp recv_into: {}", e))),
        }
    }
}

// ── wasm32: all builtins return KIND_UNSUPPORTED tuple ────────────────────

#[cfg(target_arch = "wasm32")]
mod imp {
    use super::*;

    pub fn builtin_net_tcp_connect(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_tcp_listen(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_tcp_accept(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_tcp_socket_read(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_tcp_socket_write(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_tcp_socket_drop(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(Value::Null)
    }
    pub fn builtin_net_tcp_listener_drop(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(Value::Null)
    }

    // UDP wasm32 fallbacks
    pub fn builtin_net_udp_bind(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_udp_send(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_udp_recv(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
    pub fn builtin_net_udp_drop(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(Value::Null)
    }
    pub fn builtin_net_udp_recv_into(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
        Ok(unsupported(ctx))
    }
}

pub use imp::*;

#[cfg(test)]
#[path = "network_tests.rs"]
mod network_tests;
