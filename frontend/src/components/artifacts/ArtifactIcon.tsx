import type { ArtifactKind } from '../../utils/artifacts';

/** Small line icon per artifact type (page, image, diagram, document, code). */
export function ArtifactIcon({ type, size = 18 }: { type: ArtifactKind; size?: number }) {
  const common = {
    width: size,
    height: size,
    viewBox: '0 0 24 24',
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth: 1.6,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    'aria-hidden': true,
  };
  switch (type) {
    case 'text/html':
      return (
        <svg {...common}>
          <rect x="3" y="4" width="18" height="16" rx="2" />
          <path d="M3 8h18" />
          <path d="M10 12.5 8 14.5l2 2M14 12.5l2 2-2 2" />
        </svg>
      );
    case 'image/svg+xml':
      return (
        <svg {...common}>
          <rect x="3" y="4" width="18" height="16" rx="2" />
          <circle cx="9" cy="10" r="1.6" />
          <path d="m21 16-5-5-8 9" />
        </svg>
      );
    case 'text/mermaid':
      return (
        <svg {...common}>
          <rect x="3" y="3" width="7" height="5" rx="1.2" />
          <rect x="14" y="16" width="7" height="5" rx="1.2" />
          <rect x="3" y="16" width="7" height="5" rx="1.2" />
          <path d="M6.5 8v8M10 18.5h4M6.5 12h11v4" />
        </svg>
      );
    case 'text/markdown':
      return (
        <svg {...common}>
          <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
          <path d="M14 3v5h5M9 13h6M9 17h4" />
        </svg>
      );
    default:
      return (
        <svg {...common}>
          <path d="m8 7-5 5 5 5M16 7l5 5-5 5M13.5 5l-3 14" />
        </svg>
      );
  }
}
