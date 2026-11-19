import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../lib/api';
import type { Session } from '../lib/types';
import { Button } from '../ui/controls';
import { LogoMark } from '../shell/Logo';
import { LanguageSwitch } from '../features/LanguageSwitch';

type DeviceKey = 'iphone' | 'ipad' | 'androidPhone' | 'androidTablet' | 'thisPc' | 'windowsPc' | 'mac' | 'browser';

function guessDevice(): DeviceKey {
  const ua = navigator.userAgent;
  if (/iPhone/.test(ua)) return 'iphone';
  if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'ipad';
  if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'androidPhone' : 'androidTablet';
  if (/Windows/.test(ua)) return location.hostname === 'localhost' ? 'thisPc' : 'windowsPc';
  if (/Macintosh/.test(ua)) return 'mac';
  return 'browser';
}

/** Reads and strips "#pair=123456" (from the QR code) so the code never lingers in history. */
function takeCodeFromUrl(): string | null {
  const match = /pair=(\d{6})/.exec(location.hash);
  if (!match) return null;
  history.replaceState(null, '', location.pathname + location.search);
  return match[1];
}

export function PairView({ onPaired }: { onPaired(s: Session): void }) {
  const { t } = useTranslation();
  const [code, setCode] = useState('');
  const [deviceKey] = useState(guessDevice);
  const [name, setName] = useState<string | null>(null); // null = suggested name in the current language
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const autoTried = useRef(false);
  const deviceName = name ?? t(`pair.devices.${deviceKey}`);

  const submit = async (value: string) => {
    setBusy(true);
    setError(null);
    try {
      onPaired(await api.pair(value, deviceName));
    } catch (e) {
      setError(e instanceof Error ? e.message : t('pair.failed'));
      setCode('');
    } finally {
      setBusy(false);
    }
  };

  useEffect(() => {
    if (autoTried.current) return;
    autoTried.current = true;
    const fromUrl = takeCodeFromUrl();
    if (fromUrl) {
      setCode(fromUrl);
      submit(fromUrl);
    }
  }, []);

  return (
    <div className="pair">
      <form
        className="pair-card"
        onSubmit={(e) => {
          e.preventDefault();
          if (code.length === 6) submit(code);
        }}
      >
        <LogoMark size={56} />
        <div className="pair-text">
          <h1>{t('pair.title')}</h1>
          <p className="muted">{t('pair.body')}</p>
        </div>
        <input
          className="input code-input num"
          inputMode="numeric"
          autoComplete="one-time-code"
          pattern="[0-9]*"
          maxLength={6}
          placeholder="000000"
          value={code}
          disabled={busy}
          aria-label={t('pair.code')}
          aria-invalid={!!error}
          onChange={(e) => {
            const v = e.target.value.replace(/\D/g, '').slice(0, 6);
            setCode(v);
            if (v.length === 6) submit(v);
          }}
        />
        <label className="pair-name">
          <span className="eyebrow">{t('pair.deviceName')}</span>
          <input className="input" value={deviceName} maxLength={40} onChange={(e) => setName(e.target.value)} />
        </label>
        {error && <p className="small text-danger" role="alert">{error}</p>}
        <Button type="submit" variant="primary" size="lg" block disabled={busy || code.length !== 6}>
          {busy ? t('pair.pairing') : t('pair.pair')}
        </Button>
        <p className="faint small">{t('pair.footer')}</p>
        {/* Before pairing there is no PC setting yet: this choice is kept in the browser. */}
        <LanguageSwitch local />
      </form>
    </div>
  );
}
