import { useTranslation } from 'react-i18next';
import { Check, RotateCcw } from 'lucide-react';
import { asLogoFinish, asLogoModel, DEFAULT_LOGO_FINISH, DEFAULT_LOGO_MODEL, LOGO_FINISHES, LOGO_MODELS, logoSwatch, type LogoSurface } from '../lib/logo';
import { LogoGlyph } from '../shell/Logo';
import { useDeck } from '../state/DeckContext';
import { Button } from '../ui/controls';
import { Card } from '../ui/Card';

/** Settings: choose the GameDeck icon and its color. Saved on the PC, for every device. */
export function LogoPicker() {
  const { t } = useTranslation();
  const { state, setLogo } = useDeck();
  const model = asLogoModel(state.ui.logoModel);
  const finish = asLogoFinish(state.ui.logoFinish);
  const isDefault = model === DEFAULT_LOGO_MODEL && finish === DEFAULT_LOGO_FINISH;

  const preview = (surface: LogoSurface) => (
    <div className={`logo-preview is-${surface}`}>
      <LogoGlyph model={model} finish={finish} surface={surface} size={72} />
      <span className="logo-preview-name">Game<span>Deck</span></span>
      <span className="logo-preview-note">{t(surface === 'dark' ? 'settings.logoOnDark' : 'settings.logoOnLight')}</span>
    </div>
  );

  return (
    <Card eyebrow={t('settings.logoEyebrow')} title={t('settings.logoTitle')}
      actions={!isDefault ? <Button size="sm" variant="ghost" icon={<RotateCcw size={15} />} onClick={() => setLogo(DEFAULT_LOGO_MODEL, DEFAULT_LOGO_FINISH)}>{t('settings.logoReset')}</Button> : undefined}>
      <div className="logo-previews">{preview('dark')}{preview('light')}</div>

      <div className="logo-group" role="group" aria-label={t('settings.logoColor')}>
        <h4>{t('settings.logoColor')}</h4>
        <div className="logo-finishes">
          {LOGO_FINISHES.map((id) => (
            <button key={id} type="button" className="logo-finish" aria-pressed={id === finish} onClick={() => setLogo(model, id)}>
              <span className="logo-swatch" style={{ background: logoSwatch(id) }} />
              {t(`settings.logoFinishes.${id}`)}
            </button>
          ))}
        </div>
      </div>

      <div className="logo-group" role="group" aria-label={t('settings.logoChoose')}>
        <h4>{t('settings.logoChoose')}</h4>
        <div className="logo-models">
          {LOGO_MODELS.map((id) => (
            <button key={id} type="button" className="logo-model" aria-pressed={id === model} onClick={() => setLogo(id, finish)}>
              {id === model && <span className="logo-check"><Check size={13} strokeWidth={3} /></span>}
              <LogoGlyph model={id} finish={finish} size={52} />
              <span>{t(`settings.logoModels.${id}`)}</span>
            </button>
          ))}
        </div>
      </div>

      <p className="faint small">{t('settings.logoNote')}</p>
    </Card>
  );
}
