import Foundation
import Z42VMC

/// A primitive value crossing the z42 host ABI. v0.1 supports the same
/// shapes as Tier 2 — null / i64 / f64 / bool. Strings / objects /
/// arrays land in H3 (see [`embedding.md §12 Deferred`]).
public enum Z42VMValue: Equatable {
    case null
    case i64(Int64)
    case f64(Double)
    case bool(Bool)
}

extension Z42VMValue {
    /// Convert to the raw `Z42Value` ABI struct.
    func toRaw() -> Z42Value {
        switch self {
        case .null:
            return Z42Value(tag: 0, reserved: 0, payload: 0)
        case let .i64(v):
            return Z42Value(tag: 1, reserved: 0, payload: UInt64(bitPattern: v))
        case let .f64(v):
            return Z42Value(tag: 2, reserved: 0, payload: v.bitPattern)
        case let .bool(v):
            return Z42Value(tag: 3, reserved: 0, payload: v ? 1 : 0)
        }
    }

    /// Convert from raw `Z42Value` returned by the runtime.
    /// Unsupported tags surface as `.null` (v0.1 limitation).
    static func fromRaw(_ raw: Z42Value) -> Z42VMValue {
        switch raw.tag {
        case 0: return .null
        case 1: return .i64(Int64(bitPattern: raw.payload))
        case 2: return .f64(Double(bitPattern: raw.payload))
        case 3: return .bool(raw.payload != 0)
        default: return .null
        }
    }
}
