import { useId } from 'react';
import { asLogoFinish, asLogoModel, logoMarkup, type LogoFinish, type LogoModel, type LogoSurface } from '../lib/logo';
import { useDeckOptional } from '../state/DeckContext';

/** A specific logo design, as shown in the Settings gallery. */
export function LogoGlyph({ model, finish, surface = 'dark', size = 24 }: { model: LogoModel; finish: LogoFinish; surface?: LogoSurface; size?: number }) {
  // Unique per instance: a hidden copy must not own the gradients the visible one uses.
  const prefix = `gd-${useId().replace(/[^a-zA-Z0-9]/g, '')}-`;
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" fill="none" aria-hidden style={{ overflow: 'visible', flex: 'none' }}
      dangerouslySetInnerHTML={{ __html: logoMarkup(model, finish, surface, prefix) }} />
  );
}

/** The logo chosen in Settings (the default one before pairing). */
export function LogoMark({ size = 24 }: { size?: number }) {
  const ui = useDeckOptional()?.state.ui;
  return <LogoGlyph model={asLogoModel(ui?.logoModel)} finish={asLogoFinish(ui?.logoFinish)} size={size} />;
}

export function Wordmark() {
  return (
    <span className="wordmark">
      <LogoMark size={34} />
      <span className="wordmark-text">
        Game<span className="wordmark-light">Deck</span>
      </span>
    </span>
  );
}
