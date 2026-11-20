/**
 * A picture of what is drawn in part of the page, as a PNG: every SVG in it (the charts, the map, the legend marks) with its styles written in, and the text
 * round them (legends, labels, the map's figures) placed word by word where it is on the screen, all on the page's background. Buttons are left out.
 */
const STYLE = ['fill', 'fill-opacity', 'stroke', 'stroke-width', 'stroke-opacity', 'stroke-dasharray', 'stroke-linecap', 'opacity', 'font-size', 'font-family',
  'font-weight', 'text-anchor', 'dominant-baseline', 'visibility', 'display'];
const NS = 'http://www.w3.org/2000/svg';

/** A copy of an SVG with each element's computed style written into it, so it looks the same away from the page's style sheets. */
function inlined(svg: SVGSVGElement): SVGSVGElement {
  const copy = svg.cloneNode(true) as SVGSVGElement;
  const from = [svg, ...svg.querySelectorAll('*')];
  const to = [copy, ...copy.querySelectorAll('*')];
  from.forEach((element, i) => {
    const style = getComputedStyle(element);
    const target = to[i] as SVGElement;
    target.removeAttribute('class');
    for (const p of STYLE) target.style.setProperty(p, style.getPropertyValue(p));
  });
  return copy;
}

const escape = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/** The picture as an SVG document, `title` above it. Null when there is nothing drawn. */
export function snapshotSvg(container: HTMLElement, title: string): string | null {
  const origin = container.getBoundingClientRect();
  const svgs = [...container.querySelectorAll('svg')].filter((s) => !s.closest('button') && !s.parentElement?.closest('svg'));
  if (!svgs.some((s) => s.getBoundingClientRect().width >= 100)) return null;
  const head = 28;
  const width = Math.ceil(origin.width), height = Math.ceil(origin.height) + head;
  const background = getComputedStyle(document.body).backgroundColor || '#0f0f12';
  const parts: string[] = [`<rect width="${width}" height="${height}" fill="${background}"/>`,
    `<text x="12" y="19" fill="#e2e8f0" font-size="13" font-family="system-ui, sans-serif" font-weight="600">${escape(title)}</text>`];

  for (const svg of svgs) {
    const r = svg.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;
    const copy = inlined(svg);
    copy.setAttribute('x', String(r.left - origin.left));
    copy.setAttribute('y', String(r.top - origin.top + head));
    copy.setAttribute('width', String(r.width));
    copy.setAttribute('height', String(r.height));
    parts.push(new XMLSerializer().serializeToString(copy));
  }

  // the words outside the SVGs, each where it is
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
  const range = document.createRange();
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const parent = node.parentElement;
    if (!parent || parent.closest('svg') || parent.closest('button') || parent.closest('select') || !node.textContent?.trim()) continue;
    const style = getComputedStyle(parent);
    if (style.visibility === 'hidden' || style.display === 'none') continue;
    const font = `fill="${style.color}" font-size="${style.fontSize}" font-family="${escape(style.fontFamily).replace(/"/g, "'")}" font-weight="${style.fontWeight}"`;
    const text = node.textContent;
    for (const match of text.matchAll(/\S+/g)) {
      range.setStart(node, match.index!);
      range.setEnd(node, match.index! + match[0].length);
      const r = range.getBoundingClientRect();
      if (r.width === 0 || r.right < origin.left || r.left > origin.right || r.bottom < origin.top || r.top > origin.bottom) continue;
      parts.push(`<text x="${(r.left - origin.left).toFixed(1)}" y="${(r.top - origin.top + head + r.height * 0.78).toFixed(1)}" ${font}>${escape(match[0])}</text>`);
    }
  }
  return `<svg xmlns="${NS}" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">${parts.join('')}</svg>`;
}

/** The picture as a PNG at twice the screen's size; null when there is nothing drawn. */
export async function snapshotPng(container: HTMLElement, title: string): Promise<Blob | null> {
  const svg = snapshotSvg(container, title);
  if (!svg) return null;
  const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
  try {
    const image = new Image();
    await new Promise<void>((resolve, reject) => { image.onload = () => resolve(); image.onerror = () => reject(new Error('The picture could not be drawn.')); image.src = url; });
    const canvas = document.createElement('canvas');
    canvas.width = image.width * 2;
    canvas.height = image.height * 2;
    const context = canvas.getContext('2d')!;
    context.scale(2, 2);
    context.drawImage(image, 0, 0);
    return await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'));
  } finally {
    URL.revokeObjectURL(url);
  }
}
