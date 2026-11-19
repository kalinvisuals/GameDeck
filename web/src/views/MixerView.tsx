import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { AudioLines, Cable, Headphones, Mic, MicOff, RefreshCw, Square, Volume2, VolumeX } from 'lucide-react';
import { api } from '../lib/api';
import type { AudioPatch, BusStatus } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Badge, Button, Field, Segmented, StatusDot, Switch } from '../ui/controls';
import { Card, PageHeader } from '../ui/Card';
import { Meter } from '../ui/Meter';
import { Slider } from '../ui/Slider';
import { LevelHistory } from '../ui/extras';
import type { LevelChannel } from '../state/levels';
import { DeviceSelect, RoutingCard, busTone, isCable } from '../features/audio';
import { AppsCard, ScenesSection } from '../features/scenes';

const LATENCIES = [20, 30, 40, 60, 100] as const;

export function MixerView() {
  const { t } = useTranslation();
  const { state, run, playing, stopAll, updateAudio } = useDeck();
  const { audio, devices, status } = state;
  const update = (patch: AudioPatch) => updateAudio(patch);
  const soundsLive = status.monitor.ok || status.virtual.ok;

  return (
    <div className="page">
      <PageHeader
        title={t('mixer.title')}
        subtitle={t('mixer.subtitle')}
        actions={<Button size="sm" icon={<RefreshCw size={15} />} onClick={() => run(() => api.refreshDevices())}>{t('common.rescan')}</Button>}
      />

      <div className="console">
        <Strip
          icon={<Mic size={18} strokeWidth={1.75} />}
          name={t('mixer.microphone')}
          role={t('mixer.micRole')}
          status={status.mic}
          channel="mic"
          muted={audio.micMuted}
          onMute={(m) => update({ micMuted: m })}
          muteIcons={[<MicOff key="off" size={16} />, <Mic key="on" size={16} />]}
          toggle={<Switch checked={audio.micEnabled} label={t('mixer.mixMic')} onChange={(v) => update({ micEnabled: v })} />}
        >
          <DeviceSelect label={t('mixer.inputDevice')} devices={devices.inputs.filter((d) => !isCable(d))} value={audio.micDeviceId} empty={t('common.none')} onChange={(id) => update({ micDeviceId: id })} />
          <Field label={t('mixer.gain')}>
            <Slider label={t('mixer.micGain')} value={audio.micVolume} max={2} onChange={(v) => update({ micVolume: v })} />
          </Field>
        </Strip>

        <Strip
          icon={<Cable size={18} strokeWidth={1.75} />}
          name={t('mixer.virtualMic')}
          role={t('mixer.virtualRole')}
          status={status.virtual}
          channel="virtual"
          muted={audio.virtualMuted}
          onMute={(m) => update({ virtualMuted: m })}
          muteIcons={[<VolumeX key="off" size={16} />, <Volume2 key="on" size={16} />]}
          meterNote={t('mixer.callMixNote')}
        >
          <DeviceSelect
            label={t('mixer.virtualOutput')}
            hint={t('mixer.virtualHint')}
            devices={devices.outputs.filter((d) => d.id !== audio.monitorDeviceId)}
            value={audio.virtualDeviceId}
            empty={t('common.off')}
            suggest={isCable}
            onChange={(id) => update({ virtualDeviceId: id })}
          />
          <Field label={t('mixer.soundsIntoCall')}>
            <Slider label={t('mixer.soundsIntoCall')} value={audio.virtualVolume} max={1.5} onChange={(v) => update({ virtualVolume: v })} />
          </Field>
        </Strip>

        <Strip
          icon={<Headphones size={18} strokeWidth={1.75} />}
          name={t('mixer.headphones')}
          role={t('mixer.headphonesRole')}
          status={status.monitor}
          channel="monitor"
          muted={audio.monitorMuted}
          onMute={(m) => update({ monitorMuted: m })}
          muteIcons={[<VolumeX key="off" size={16} />, <Volume2 key="on" size={16} />]}
          toggle={<Switch checked={audio.monitorEnabled} label={t('mixer.monitor')} onChange={(v) => update({ monitorEnabled: v })} />}
          meterNote={t('mixer.neverVoice')}
        >
          <DeviceSelect label={t('mixer.outputDevice')} devices={devices.outputs.filter((d) => !isCable(d))} value={audio.monitorDeviceId} empty={t('common.windowsDefault')} onChange={(id) => update({ monitorDeviceId: id })} />
          <Field label={t('mixer.soundsInHeadphones')}>
            <Slider label={t('mixer.soundsInHeadphones')} value={audio.monitorVolume} max={1.5} onChange={(v) => update({ monitorVolume: v })} />
          </Field>
        </Strip>

        <Strip
          icon={<AudioLines size={18} strokeWidth={1.75} />}
          name={t('mixer.soundboard')}
          role={t('mixer.soundboardRole')}
          status={{ enabled: true, ok: soundsLive, deviceName: null, error: soundsLive ? null : t('mixer.noOutput') }}
          channel="sounds"
          meterNote={t('mixer.padsNote')}
        >
          <div className="strip-stat">
            <div>
              <span className="eyebrow">{t('mixer.playingNow')}</span>
              <span className="num strip-stat-value">{playing.length}</span>
            </div>
            <Button size="sm" variant="danger" icon={<Square size={11} fill="currentColor" strokeWidth={0} />} disabled={playing.length === 0} onClick={stopAll}>
              {t('common.stopAll')}
            </Button>
          </div>
          <p className="faint small">{t('mixer.padVolumeNote')}</p>
        </Strip>
      </div>

      <AppsCard />
      <ScenesSection />

      <div className="mixer-lower">
        <RoutingCard />
        <Card eyebrow={t('mixer.engine')} title={t('mixer.outputBuffer')}>
          <Field label={t('mixer.latency')} hint={t('mixer.latencyHint')}>
            <Segmented
              size="sm"
              label={t('mixer.outputBuffer')}
              value={String(audio.latencyMs)}
              onChange={(v) => update({ latencyMs: Number(v) })}
              options={LATENCIES.map((ms) => ({ value: String(ms), label: `${ms}` }))}
            />
          </Field>
          <p className="faint small num">
            {t('mixer.micBuffer', { underruns: state.diagnostics.mic.underruns, drops: state.diagnostics.mic.drops, ms: state.diagnostics.mic.bufferedMs.toFixed(0) })}
          </p>
        </Card>
      </div>
    </div>
  );
}

function Strip(props: {
  icon: ReactNode;
  name: string;
  role: string;
  status: BusStatus;
  channel: LevelChannel;
  muted?: boolean;
  onMute?(m: boolean): void;
  muteIcons?: [ReactNode, ReactNode];
  toggle?: ReactNode;
  meterNote?: string;
  children: ReactNode;
}) {
  const { t } = useTranslation();
  const { tone, label } = busTone(props.status);
  const muted = !!props.muted && props.status.enabled;
  return (
    <section className={`strip ${props.status.enabled ? '' : 'is-off'} ${muted ? 'is-muted' : ''}`} aria-label={props.name}>
      <header className="strip-head">
        <span className="strip-icon">{props.icon}</span>
        <div className="strip-title">
          <h3>{props.name}</h3>
          <span className="strip-role">{props.role}</span>
        </div>
        {props.toggle}
      </header>

      <div className="strip-state small">
        <StatusDot tone={muted ? 'warn' : tone} />
        <span>{muted ? t('common.muted') : label}</span>
        {props.status.enabled && props.status.deviceName && <span className="faint strip-device">· {props.status.deviceName}</span>}
        {muted && <Badge tone="warn">{t('common.muted')}</Badge>}
      </div>

      <div className="strip-viz">
        <LevelHistory channel={props.channel} label={props.name} height={44} />
        <Meter channel={props.channel} active={props.status.ok && !muted} label={t('common.levelOf', { name: props.name })} />
        {props.meterNote && <span className="strip-note faint">{props.meterNote}</span>}
      </div>

      {props.status.enabled && props.status.error && <p className="small text-danger">{props.status.error}</p>}

      <div className="strip-body">{props.children}</div>

      {props.onMute && props.muteIcons && (
        <button
          type="button"
          className={`mute-btn ${muted ? 'is-muted' : ''}`}
          aria-pressed={!!props.muted}
          onClick={() => props.onMute!(!props.muted)}
          disabled={!props.status.enabled}
        >
          {props.muted ? props.muteIcons[0] : props.muteIcons[1]}
          <span>{props.muted ? t('mixer.unmute') : t('mixer.mute')}</span>
        </button>
      )}
    </section>
  );
}
