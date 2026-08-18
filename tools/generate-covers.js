/*
 * Generates one cover per book.
 *
 * The catalogue shipped with every title pointing at the same book2.png, and the twenty stock
 * photos in wwwroot/images/User/Book cover eight subjects between them — mapping fifty titles onto
 * those would still repeat each image two or three times. A typographic cover is generated per
 * title instead, so every book is visually distinct and the artwork always matches the record.
 *
 * SVG rather than raster: a few kilobytes each, sharp at any size, and no binary blobs in git.
 *
 * Usage:  node tools/generate-covers.js <books.tsv> <outputDir>
 * where each input line is  Id~Title~Author~Category~BookCode
 */

const fs = require('fs');
const path = require('path');

const [, , inputPath, outDir] = process.argv;

if (!inputPath || !outDir) {
    console.error('usage: node tools/generate-covers.js <books.tsv> <outputDir>');
    process.exit(1);
}

// One palette per subject, so a shelf of Mathematics reads as a set while still
// giving each title its own shade. [dark spine, field, accent]
const PALETTES = {
    'Programming':            ['#1b2a41', '#24405e', '#f07c20'],
    'Mathematics':            ['#14342b', '#1e5245', '#7fd1ae'],
    'Law':                    ['#3a1f1f', '#5c3030', '#e0a458'],
    'Finance and Accounting': ['#2b2118', '#4a3826', '#d4a017'],
    'Science Fiction':        ['#1a1633', '#2d2559', '#8b7ef8'],
    'Mystery':                ['#1c1c1c', '#333333', '#c0392b'],
    'Romance':                ['#3a1730', '#5c2649', '#f19cbb'],
    'Uncategorized':          ['#26221f', '#403a35', '#f07c20']
};

const slug = s => s.toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');

const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** Greedy wrap on whole words; long titles get smaller type rather than overflowing. */
function wrap(text, maxChars) {
    const words = text.split(' ');
    const lines = [];
    let line = '';

    for (const word of words) {
        const candidate = line ? `${line} ${word}` : word;
        if (candidate.length > maxChars && line) {
            lines.push(line);
            line = word;
        } else {
            line = candidate;
        }
    }

    if (line) lines.push(line);
    return lines;
}

fs.mkdirSync(outDir, { recursive: true });

const lines = fs.readFileSync(inputPath, 'utf8').split('\n').map(l => l.trim()).filter(Boolean);
const mapping = [];

for (const raw of lines) {
    const [id, title, author, category, bookCode] = raw.split('~');
    if (!id || !title) continue;

    const [spine, field, accent] = PALETTES[category] || PALETTES['Uncategorized'];

    // Longer titles wrap more and shrink, so the block always fits the same box.
    const titleLines = wrap(title, title.length > 34 ? 16 : 14);
    const fontSize = titleLines.length > 3 ? 22 : titleLines.length > 2 ? 26 : 30;
    const startY = 150 - ((titleLines.length - 1) * fontSize * 0.6);

    const titleSpans = titleLines
        .map((l, i) => `<tspan x="150" y="${Math.round(startY + i * fontSize * 1.2)}">${esc(l)}</tspan>`)
        .join('');

    const authorLines = wrap(author, 22);
    const authorSpans = authorLines
        .map((l, i) => `<tspan x="150" y="${300 + i * 18}">${esc(l)}</tspan>`)
        .join('');

    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 400" width="300" height="400" role="img" aria-label="${esc(title)} by ${esc(author)}">
  <defs>
    <linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="${field}"/>
      <stop offset="1" stop-color="${spine}"/>
    </linearGradient>
  </defs>
  <rect width="300" height="400" fill="url(#g)"/>
  <rect x="0" y="0" width="14" height="400" fill="${spine}"/>
  <rect x="14" y="0" width="2" height="400" fill="${accent}" opacity="0.55"/>
  <rect x="42" y="46" width="46" height="3" fill="${accent}"/>
  <text x="150" y="${0}" fill="#ffffff" font-family="Georgia, 'Times New Roman', serif" font-size="${fontSize}" font-weight="700" text-anchor="middle">${titleSpans}</text>
  <text x="150" y="0" fill="${accent}" font-family="Georgia, 'Times New Roman', serif" font-size="15" font-style="italic" text-anchor="middle">${authorSpans}</text>
  <text x="150" y="360" fill="#ffffff" font-family="'Courier New', monospace" font-size="12" letter-spacing="1" text-anchor="middle" opacity="0.75">${esc(bookCode || '')}</text>
  <text x="150" y="382" fill="#ffffff" font-family="Arial, sans-serif" font-size="9" letter-spacing="2" text-anchor="middle" opacity="0.45">${esc((category || '').toUpperCase())}</text>
</svg>
`;

    const name = `${slug(title)}.svg`;
    fs.writeFileSync(path.join(outDir, name), svg, 'utf8');
    mapping.push({ id: Number(id), file: name });
}

console.log(JSON.stringify(mapping));
