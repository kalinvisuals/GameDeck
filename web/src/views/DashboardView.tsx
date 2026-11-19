import type { CSSProperties, ReactNode } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import i18n from 'i18next';
import { ArrowRight, Cable, Headphones, LayoutGrid, Mic, Square, Star, Wifi } from 'lucide-react';
import { SoundIcon } from '../lib/icons';
import type { BusStatus } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Badge, Button, IconButton, StatusDot, type Tone } from '../ui/controls';
import { Card, EmptyState, PageHeader } from '../ui/Card';
import { LevelHistory } from '../ui/extras';
import { Meter } from '../ui/Meter';
import { RoutingCard, busTone } from '../features/audio';
import type { ViewId } from '../shell/AppShell';

const uptime = (iso: string) => {
  const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  return m < 60 ? i18n.t('dashboard.uptimeMin', { m }) : i18n.t('dashboard.uptimeHours', { h: Math.floor(m / 60), m: m % 60 });
};

export function DashboardView({ go }: { go(v: ViewId): void }) {
  const { t } = useTranslation();
  const { state, connected, playing, playingBySound, soundsById, stop, trigger, profile } = useDeck();
  const { status, audio } = state;

  const favorites = state.sounds.filter((s) => s.favorite);
  const quick = (favorites.length ? favorites : profile.layout.map((id) => soundsById.get(id)).filter((s) => s !== undefined)).slice(0, 8);

  return (
    <div className="page">
      <PageHeader
        title={t('dashboard.title')}
        subtitle={<Trans i18nKey="dashboard.subtitle" values={{ name: profile.name }} components={{ b: <b className="text-strong" /> }} />}
        actions={<Button variant="primary" size="sm" icon={<LayoutGrid size={15} />} onClick={() => go('soundboard')}>{t('dashboard.openSoundboard')}</Button>}
      />

      <div className="status-grid">
        <StatusCard icon={<Wifi size={18} strokeWidth={1.75} />} name={t('dashboard.controlLink')} tone={connected ? 'ok' : 'warn'} state={connected ? t('nav.connected') : t('nav.reconnectingShort')}
          detail={state.server.urls[0]?.replace('http://', '') ?? t('dashboard.noLan')} />
        <StatusCard icon={<Mic size={18} strokeWidth={1.75} />} name={t('dashboard.microphone')}
          {...(audio.micMuted && status.mic.enabled ? { tone: 'warn' as Tone, state: t('common.muted'), detail: status.mic.deviceName ?? t('common.dash') } : fromBus(status.mic, t('dashboard.micOff')))} />
        <StatusCard icon={<Cable size={18} strokeWidth={1.75} />} name={t('dashboard.virtualMic')}
          {...fromBus(status.virtual, t('dashboard.virtualOff'))} />
        <StatusCard icon={<Headphones size={18} strokeWidth={1.75} />} name={t('dashboard.headphones')} {...fromBus(status.monitor, t('dashboard.monitorOff'))} />
      </div>

      <div className="dash-grid">
        <Card className="dash-activity" eyebrow={t('dashboard.activityEyebrow')} title={t('dashboard.activity')} actions={<Button size="sm" variant="ghost" onClick={() => go('mixer')}>{t('dashboard.mixerLink')} <ArrowRight size={14} /></Button>}>
          <div className="activity-rows">
            <ActivityRow label={t('dashboard.soundboardRow')} channel="sounds" active={status.monitor.ok || status.virtual.ok} />
            <ActivityRow label={t('dashboard.microphone')} channel="mic" active={status.mic.ok && !audio.micMuted} />
            <ActivityRow label={t('dashboard.callMix')} channel="virtual" active={status.virtual.ok} />
          </div>
        </Card>

        <Card eyebrow={t('dashboard.nowEyebrow')} title={t('dashboard.playing')} actions={playing.length > 0 && <Badge tone="accent">{playing.length}</Badge>}>
          {playing.length === 0 ? (
            <EmptyState compact icon={<Square size={16} strokeWidth={1.5} />} title={t('dashboard.silence')}>{t('dashboard.silenceBody')}</EmptyState>
          ) : (
            <ul className="playing-list">
              {playing.map((p) => {
                const s = soundsById.get(p.soundId);
                const elapsed = p.elapsedMs + (performance.now() - p.receivedAt);
                return (
                  <li key={p.instanceId} style={{ '--dur': `${Math.max(p.durationMs, 1)}ms`, '--delay': `-${Math.round(elapsed)}ms` } as CSSProperties}>
                    <span className="playing-icon"><SoundIcon name={s?.icon ?? 'music'} size={18} /></span>
                    <div className="playing-body">
                      <span className="playing-name">{s?.label ?? t('dashboard.soundFallback')}</span>
                      <span className="playing-bar"><span className={p.loop ? 'is-loop' : ''} /></span>
                    </div>
                    <IconButton label={t('dashboard.stopSound', { name: s?.label ?? t('dashboard.soundFallback') })} icon={<Square size={12} fill="currentColor" strokeWidth={0} />} onClick={() => stop(p.soundId)} />
                  </li>
                );
              })}
            </ul>
          )}
        </Card>

        <Card className="dash-quick" eyebrow={favorites.length ? t('dashboard.favoritesEyebrow') : profile.name} title={t('dashboard.quickPads')}
          actions={<Button size="sm" variant="ghost" onClick={() => go('soundboard')}>{t('dashboard.allPads')} <ArrowRight size={14} /></Button>}>
          {quick.length === 0 ? (
            <EmptyState compact icon={<Star size={16} strokeWidth={1.5} />} title={t('dashboard.noPads')}>{t('dashboard.noPadsBody')}</EmptyState>
          ) : (
            <div className="quick-grid">
              {quick.map((s) => (
                <button key={s.id} className={`quick-pad ${playingBySound.has(s.id) ? 'is-playing' : ''}`} style={{ ['--tag' as string]: s.color }} onClick={() => trigger(s.id)} aria-label={s.label}>
                  <SoundIcon name={s.icon} size={18} />
                  <span>{s.label}</span>
                </button>
              ))}
            </div>
          )}
        </Card>

        <RoutingCard compact />

        <Card eyebrow={t('dashboard.systemEyebrow')} title={t('dashboard.thisPc')}>
          <dl className="facts">
            <div><dt>{t('dashboard.version')}</dt><dd className="num">{state.server.version}</dd></div>
            <div><dt>{t('dashboard.uptime')}</dt><dd className="num">{uptime(state.diagnostics.startedUtc)}</dd></div>
            <div><dt>{t('dashboard.sounds')}</dt><dd className="num">{state.sounds.length}</dd></div>
            <div><dt>{t('dashboard.profiles')}</dt><dd className="num">{state.profiles.length}</dd></div>
            <div><dt>{t('dashboard.pairedDevices')}</dt><dd className="num">{state.pairedDevices.length}</dd></div>
            <div><dt>{t('dashboard.outputBuffer')}</dt><dd className="num">{t('dashboard.ms', { value: audio.latencyMs })}</dd></div>
          </dl>
        </Card>
      </div>
    </div>
  );
}

function fromBus(b: BusStatus, offText: string) {
  const { tone, label } = busTone(b);
  return { tone, state: label, detail: b.enabled ? (b.error ?? b.deviceName ?? i18n.t('common.dash')) : offText, error: b.enabled && !!b.error };
}

function StatusCard(props: { icon: ReactNode; name: string; tone: Tone; state: string; detail: string; error?: boolean }) {
  return (
    <div className={`status-card tone-${props.tone}`}>
      <div className="status-card-top">
        <span className="status-card-icon">{props.icon}</span>
        <span className="status-card-state small">
          <StatusDot tone={props.tone} pulse={props.tone === 'warn'} />
          {props.state}
        </span>
      </div>
      <span className="status-card-name">{props.name}</span>
      <span className={`status-card-detail small ${props.error ? 'text-danger' : 'faint'}`} title={props.detail}>{props.detail}</span>
    </div>
  );
}

function ActivityRow({ label, channel, active }: { label: string; channel: 'mic' | 'sounds' | 'virtual'; active: boolean }) {
  const { t } = useTranslation();
  return (
    <div className={`activity-row ${active ? '' : 'is-idle'}`}>
      <div className="activity-label">
        <span className="small">{label}</span>
        <span className="faint small">{active ? t('common.live') : t('common.idle')}</span>
      </div>
      <LevelHistory channel={channel} label={label} height={40} />
      <Meter channel={channel} active={active} label={t('common.levelOf', { name: label })} />
    </div>
  );
}
