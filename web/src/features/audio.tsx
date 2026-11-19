import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import i18n from 'i18next';
import { CheckCircle2, CircleAlert, Info, TriangleAlert } from 'lucide-react';
import { api } from '../lib/api';
import type { AudioDevice, BusStatus, RouteTestResult } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Button, Field, Select, type Tone } from '../ui/controls';
import { Card } from '../ui/Card';

export function busTone(b: BusStatus): { tone: Tone; label: string } {
  if (!b.enabled) return { tone: 'off', label: i18n.t('common.off') };
  return b.ok ? { tone: 'ok', label: i18n.t('common.active') } : { tone: 'bad', label: i18n.t('common.problem') };
}

export function DeviceSelect(props: {
  label: string;
  hint?: string;
  devices: AudioDevice[];
  value: string | null;
  empty: string;
  suggest?(d: AudioDevice): boolean;
  onChange(id: string): void;
}) {
  const { t } = useTranslation();
  const known = props.value === null || props.devices.some((d) => d.id === props.value);
  const sorted = props.suggest ? [...props.devices].sort((a, b) => Number(props.suggest!(b)) - Number(props.suggest!(a))) : props.devices;
  return (
    <Field label={props.label} hint={props.hint}>
      <Select aria-label={props.label} value={props.value ?? ''} onChange={(e) => props.onChange(e.target.value)}>
        <option value="">{props.empty}</option>
        {!known && <option value={props.value!}>{t('common.disconnectedDevice')}</option>}
        {sorted.map((d) => (
          <option key={d.id} value={d.id}>
            {props.suggest?.(d) ? '★ ' : ''}
            {d.name}
            {d.isDefault ? t('common.defaultTag') : ''}
          </option>
        ))}
      </Select>
    </Field>
  );
}

export const isCable = (d: AudioDevice) => /cable|virtual|voicemeeter/i.test(d.name + d.adapter);

/** Server-side routing report plus the real tone-through-the-cable test. */
export function RoutingCard({ compact }: { compact?: boolean }) {
  const { t } = useTranslation();
  const { state, run } = useDeck();
  const { routing, status } = state;
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<RouteTestResult | null>(null);
  const canTest = !!routing.selected?.capture && status.virtual.ok;
  const problems = routing.issues.filter((i) => i.level !== 'info');

  return (
    <Card
      className="routing-card"
      eyebrow={t('routing.eyebrow')}
      title={problems.length === 0 && status.virtual.ok ? t('routing.ok') : t('routing.attention')}
      actions={
        <Button
          size="sm"
          variant="primary"
          disabled={!canTest || busy}
          title={t('routing.testTip')}
          onClick={() =>
            run(async () => {
              setBusy(true);
              try {
                setResult(await api.routeTest());
              } finally {
                setBusy(false);
              }
            })
          }
        >
          {busy ? t('routing.testing') : t('routing.test')}
        </Button>
      }
    >
      <ul className="routing-list">
        {(compact ? routing.issues.slice(0, 3) : routing.issues).map((i, n) => {
          const Icon = i.level === 'error' ? CircleAlert : i.level === 'warning' ? TriangleAlert : Info;
          return (
            <li key={n} className={`routing-item is-${i.level}`}>
              <Icon size={16} strokeWidth={1.75} aria-hidden />
              <div>
                <span>{i.message}</span>
                {i.fix && <span className="routing-fix">→ {i.fix}</span>}
              </div>
            </li>
          );
        })}
      </ul>
      {result && (
        <div className={`route-result ${result.detected && result.pitchOk ? 'is-ok' : 'is-bad'}`} role="status">
          {result.detected && result.pitchOk ? <CheckCircle2 size={16} /> : <CircleAlert size={16} />}
          <span>{result.message}</span>
        </div>
      )}
      {!canTest && <p className="faint small">{t('routing.unavailable')}</p>}
    </Card>
  );
}
