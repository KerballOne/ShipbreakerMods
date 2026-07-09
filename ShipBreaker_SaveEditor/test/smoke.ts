import { fnv1a32 } from "../src/lpw/fnv1a32";
import { read7BitLen, write7BitLen, readLengthPrefixedString, writeLengthPrefixedString } from "../src/lpw/binary";

const knownHashes: [string, number][] = [
  ["ActionTrackerData", 2386273737],
  ["MessageData", 1536684268],
  ["GeneralData", 244336563],
  ["ProfileDifficultyData", 2778834987],
  ["HabData", 3166098152],
];

let failures = 0;
for (const [name, expected] of knownHashes) {
  const actual = fnv1a32(name);
  if (actual !== expected) {
    console.error(`FAIL fnv1a32("${name}") = ${actual}, expected ${expected}`);
    failures++;
  } else {
    console.log(`OK   fnv1a32("${name}") = ${actual}`);
  }
}

// 7-bit length prefix round trip
const testStrings = ["", "heidi", "Beltalowda", "a".repeat(200)];
for (const s of testStrings) {
  const encoded = writeLengthPrefixedString(s);
  const { value, newPos } = readLengthPrefixedString(encoded, 0);
  if (value !== s || newPos !== encoded.length) {
    console.error(`FAIL length-prefix round-trip for "${s.slice(0, 20)}..." got "${value}"`);
    failures++;
  } else {
    console.log(`OK   length-prefix round-trip for string of length ${s.length}`);
  }
}

if (failures > 0) {
  console.error(`\n${failures} failure(s)`);
  process.exit(1);
}
console.log("\nAll smoke checks passed.");
