import { useState } from 'react';

interface VisionGalleryProps {
  screenshots: string[]; // base64 JPEG payloads
}

export function VisionGallery({ screenshots }: VisionGalleryProps) {
  const [active, setActive] = useState<string | null>(null);

  return (
    <div className="gallery">
      {screenshots.map((b64, i) => (
        <button
          key={i}
          className="gallery__card"
          onClick={() => setActive(b64)}
          type="button"
          aria-label={`Screenshot ${i + 1}`}
        >
          <img src={`data:image/jpeg;base64,${b64}`} alt={`Screenshot ${i + 1}`} />
        </button>
      ))}

      {active && (
        <div className="modal" onClick={() => setActive(null)}>
          <img
            className="modal__img"
            src={`data:image/jpeg;base64,${active}`}
            alt="Screenshot preview"
          />
        </div>
      )}
    </div>
  );
}
