import { useEffect, useRef, useState } from 'react';
import { usePrefs } from '../../state/prefs';

const STILL_MAX_PX = 256;
const REDUCED = '(prefers-reduced-motion: reduce)';

/**
 * Whether deck animations may play on this device: the Smart Deck setting is on, and neither the
 * dashboard's "Reduce motion" nor the system's reduced-motion preference is set.
 */
export function useDeckMotion(): boolean {
  const [prefs] = usePrefs();
  const [systemReduced, setSystemReduced] = useState(() => window.matchMedia?.(REDUCED).matches ?? false);
  useEffect(() => {
    const query = window.matchMedia?.(REDUCED);
    if (!query) return;
    const onChange = () => setSystemReduced(query.matches);
    query.addEventListener('change', onChange);
    return () => query.removeEventListener('change', onChange);
  }, []);
  return prefs.deckAnimations && !prefs.reduceMotion && !systemReduced;
}

export const deckImageUrl = (name: string) => `/api/deck/images/${name}`;

/**
 * A button image. When it may animate, the browser plays it natively (GIF, WebP and APNG decode in the
 * browser's own image pipeline, which stops painting when the page is hidden). Otherwise its first frame
 * is drawn once onto a small canvas and nothing runs after that.
 */
export function DeckImage({ name, animate, className = 'deck-key-image' }: { name: string; animate: boolean; className?: string }) {
  if (animate) return <img className={className} src={deckImageUrl(name)} alt="" draggable={false} decoding="async" />;
  return <StillFrame className={className} src={deckImageUrl(name)} />;
}

function StillFrame({ src, className }: { src: string; className: string }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    let live = true;
    const img = new Image();
    img.decoding = 'async';
    img.src = src;
    img
      .decode()
      .then(() => {
        const c = canvas.current;
        if (!live || !c || !img.naturalWidth) return;
        const scale = Math.min(1, STILL_MAX_PX / Math.max(img.naturalWidth, img.naturalHeight));
        c.width = Math.max(1, Math.round(img.naturalWidth * scale));
        c.height = Math.max(1, Math.round(img.naturalHeight * scale));
        c.getContext('2d')?.drawImage(img, 0, 0, c.width, c.height);
      })
      .catch(() => undefined); // a broken image just stays empty, like a broken <img>
    return () => {
      live = false;
      img.src = '';
    };
  }, [src]);
  return <canvas ref={canvas} className={`${className} is-still`} aria-hidden data-still />;
}

/**
 * Turns an SVG into a 256 px PNG in the browser before upload: drawn as an image, an SVG runs no
 * scripts and loads nothing external, so only its pixels reach the PC. Other files pass through.
 */
export async function prepareUpload(file: File): Promise<File> {
  const isSvg = file.type === 'image/svg+xml' || /\.svg$/i.test(file.name);
  if (!isSvg) return file;
  // A data: URL, because the dashboard's content policy allows images only from the PC and data: (not blob:).
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  const url = `data:image/svg+xml;base64,${btoa(binary)}`;
  {
    const img = new Image();
    img.src = url;
    await img.decode();
    const size = STILL_MAX_PX;
    const canvas = document.createElement('canvas');
    canvas.width = size;
    canvas.height = size;
    const w = img.naturalWidth || size;
    const h = img.naturalHeight || size;
    const scale = Math.min(size / w, size / h);
    canvas.getContext('2d')!.drawImage(img, (size - w * scale) / 2, (size - h * scale) / 2, w * scale, h * scale);
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'));
    if (!blob) throw new Error('svg');
    return new File([blob], file.name.replace(/\.svg$/i, '.png'), { type: 'image/png' });
  }
}
