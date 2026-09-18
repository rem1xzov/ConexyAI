import { useState } from 'react';

interface VisionGalleryProps {
  screenshots: string[]; // base64 JPEG payloads
  onClear: () => void;
}

export function VisionGallery({ screenshots, onClear }: VisionGalleryProps) {
  const [active, setActive] = useState<string | null>(null);

  return (
    <section className="panel">
      <header className="panel__header">
        <h2>Vision</h2>
        <div className="panel__actions">
          <span className="badge badge--muted">{screenshots.length}</span>
          {screenshots.length > 0 && (
            <button className="btn btn--ghost" onClick={onClear} type="button">
              Clear
            </button>
          )}
        </div>
      </header>

      {screenshots.length === 0 ? (
        <p className="muted">Playwright screenshots will appear here…</p>
      ) : (
        <div className="gallery">
          {screenshots.map((b64, i) => (
            <button
              key={i}
              className="gallery__item"
              onClick={() => setActive(b64)}
              type="button"
            >
              <img src={`data:image/jpeg;base64,${b64}`} alt={`Screenshot ${i + 1}`} />
            </button>
          ))}
        </div>
      )}

      {active && (
        <div className="modal" onClick={() => setActive(null)}>
          <img className="modal__image" src={`data:image/jpeg;base64,${active}`} alt="Preview" />
        </div>
      )}
    </section>
  );
}
