// LOGO_SWEEP: добавлено 2026-09-20
/**
 * ConexyAI mark: two upward chevrons with a light band that sweeps along the contour.
 *
 * The band is a `stroke-dasharray` window on a second copy of each path, animated with
 * `stroke-dashoffset`. Both paths run bottom-left -> apex -> bottom-right, so the sweep
 * follows the bend for free, and `pathLength={100}` normalises the dash maths.
 *
 * Geometry is taken from `src/assets/conexyai.svg` (640x640 viewBox).
 */
interface ChevronSpec {
  id: string;
  d: string;
  /** Staggered so the lower chevron leads: the highlight reads as a rising wave. */
  delayClass: string;
}

const CHEVRONS: ChevronSpec[] = [
  { id: 'chevron-bottom', d: 'M 100 450 L 320 305 L 540 450', delayClass: '' },
  { id: 'chevron-top', d: 'M 100 280 L 320 135 L 540 280', delayClass: 'conexy-logo__band--delayed' },
];

const STROKE_WIDTH = 72;

export interface ConexyLogoProps {
  /** Rendered width/height in px (the square viewBox scales with it). */
  size?: number;
  /** Idle: static mark. Active (thinking / generating): the light band keeps running. */
  active?: boolean;
  className?: string;
}

export function ConexyLogo({ size = 88, active = false, className }: ConexyLogoProps) {
  return (
    <svg
      className={`conexy-logo ${active ? 'conexy-logo--active' : ''} ${className ?? ''}`.trim()}
      viewBox="0 0 640 640"
      width={size}
      height={size}
      role="img"
      aria-label="ConexyAI"
    >
      {/* Static chevrons: the mark itself, always visible. */}
      <g fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth={STROKE_WIDTH}>
        {CHEVRONS.map((c) => (
          <path key={c.id} id={c.id} className="conexy-logo__track" d={c.d} />
        ))}
      </g>

      {/* Soft halo under the band, so the highlight reads as light rather than a dash. */}
      <g fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth={STROKE_WIDTH}>
        {CHEVRONS.map((c) => (
          <path
            key={`${c.id}-glow`}
            className={`conexy-logo__glow ${c.delayClass}`.trim()}
            d={c.d}
            pathLength={100}
          />
        ))}
      </g>

      {/* Travelling highlight. */}
      <g fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth={STROKE_WIDTH}>
        {CHEVRONS.map((c) => (
          <path
            key={`${c.id}-band`}
            className={`conexy-logo__band ${c.delayClass}`.trim()}
            d={c.d}
            pathLength={100}
          />
        ))}
      </g>
    </svg>
  );
}
