/**
 * Low-level binary readers/writers for the .lpw save format. Ported from
 * SaveEditor/lpw_tool.py (read_7bit_len/write_7bit_len, struct field reads).
 *
 * Signedness matters here and is easy to get backwards: hashedDataKey and all
 * asset/message hashes are UNSIGNED 32-bit (readUInt32LE/writeUInt32LE) -- never
 * sign-extend a hash. ActionTrackerData values, certification tier fields, and
 * shifts_until_polaris are SIGNED 32-bit (readInt32LE/writeInt32LE). Getting this
 * wrong silently corrupts any value >= 0x80000000 without crashing.
 */

/** .NET BinaryWriter-style 7-bit-encoded length prefix. Continuation bit is the
 * high bit (0x80), little-endian bit order (first byte holds the low 7 bits).
 * Operates on byte length, not character count -- caller must pass UTF-8 byte
 * length when used for strings. */
export function read7BitLen(data: Buffer, pos: number): { value: number; newPos: number } {
  let result = 0;
  let shift = 0;
  let p = pos;
  for (;;) {
    const b = data[p];
    p += 1;
    result |= (b & 0x7f) << shift;
    if ((b & 0x80) === 0) break;
    shift += 7;
  }
  return { value: result >>> 0, newPos: p };
}

export function write7BitLen(n: number): Buffer {
  const out: number[] = [];
  let value = n;
  for (;;) {
    const b = value & 0x7f;
    value >>>= 7;
    if (value) {
      out.push(b | 0x80);
    } else {
      out.push(b);
      break;
    }
  }
  return Buffer.from(out);
}

/** Read a .NET 7-bit-length-prefixed UTF-8 string. Returns the decoded string and
 * the position just past it. */
export function readLengthPrefixedString(data: Buffer, pos: number): { value: string; newPos: number } {
  const { value: byteLen, newPos: strStart } = read7BitLen(data, pos);
  const strEnd = strStart + byteLen;
  const value = data.subarray(strStart, strEnd).toString("utf8");
  return { value, newPos: strEnd };
}

/** Encode a string as a .NET 7-bit-length-prefixed UTF-8 string -- byte length,
 * not JS string .length, is what gets prefixed. */
export function writeLengthPrefixedString(s: string): Buffer {
  const strBytes = Buffer.from(s, "utf8");
  return Buffer.concat([write7BitLen(strBytes.length), strBytes]);
}
