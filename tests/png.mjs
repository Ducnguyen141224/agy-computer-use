import { inflateSync, deflateSync } from 'node:zlib';

/** A disposable 2x2 solid input texture, encoded only in RAM. */
export function solidPng(red, green, blue) {
  const chunk = (type, bytes) => {
    const name = Buffer.from(type); const body = Buffer.concat([name, bytes]); let crc = 0xFFFFFFFF;
    for (const byte of body) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xEDB88320 : 0); }
    const result = Buffer.alloc(bytes.length + 12); result.writeUInt32BE(bytes.length, 0); body.copy(result, 4); result.writeUInt32BE((crc ^ 0xFFFFFFFF) >>> 0, result.length - 4); return result;
  };
  const header = Buffer.alloc(13); header.writeUInt32BE(2, 0); header.writeUInt32BE(2, 4); header[8] = 8; header[9] = 2;
  const row = Buffer.from([0, red, green, blue, red, green, blue]);
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.concat([row, row]))), chunk('IEND', Buffer.alloc(0))]).toString('base64');
}

/** Decode 8-bit RGB/RGBA PNGs for in-memory capture assertions, without writing media. */
export function pixel(data, x = 0, y = 0) {
  const png = Buffer.from(data, 'base64');
  if (png.subarray(1, 4).toString() !== 'PNG') throw new Error('Not PNG');
  let width, height, channels;
  const chunks = [];
  for (let offset = 8; offset < png.length;) {
    const size = png.readUInt32BE(offset);
    const type = png.toString('ascii', offset + 4, offset + 8);
    const chunk = png.subarray(offset + 8, offset + 8 + size);
    if (type === 'IHDR') {
      width = chunk.readUInt32BE(0); height = chunk.readUInt32BE(4);
      if (chunk[8] !== 8 || ![2, 6].includes(chunk[9]) || chunk[12] !== 0) throw new Error('Unsupported test PNG format');
      channels = chunk[9] === 6 ? 4 : 3;
    }
    if (type === 'IDAT') chunks.push(chunk);
    offset += size + 12;
  }
  if (x >= width || y >= height) throw new Error('Pixel outside image');
  const bytes = inflateSync(Buffer.concat(chunks));
  const stride = width * channels;
  let prior = Buffer.alloc(stride), row;
  for (let rowIndex = 0; rowIndex <= y; rowIndex++) {
    const start = rowIndex * (stride + 1);
    const filter = bytes[start];
    row = Buffer.alloc(stride);
    for (let index = 0; index < stride; index++) {
      const left = index >= channels ? row[index - channels] : 0;
      const above = prior[index];
      const upperLeft = index >= channels ? prior[index - channels] : 0;
      let predict = 0;
      if (filter === 1) predict = left;
      else if (filter === 2) predict = above;
      else if (filter === 3) predict = Math.floor((left + above) / 2);
      else if (filter === 4) {
        const p = left + above - upperLeft;
        const a = Math.abs(p - left), b = Math.abs(p - above), c = Math.abs(p - upperLeft);
        predict = a <= b && a <= c ? left : b <= c ? above : upperLeft;
      } else if (filter !== 0) throw new Error('Unsupported PNG filter');
      row[index] = (bytes[start + 1 + index] + predict) & 255;
    }
    prior = row;
  }
  return [...row.subarray(x * channels, x * channels + 3)];
}
