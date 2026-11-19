import { useState, type ReactNode } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { CheckCircle2, CircleAlert, Download, TriangleAlert } from 'lucide-react';
import type { AudioDevice, EndpointInfo } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Button, Segmented } from '../ui/controls';
import { Card, PageHeader } from '../ui/Card';
import { RoutingCard } from '../features/audio';
import type { ViewId } from '../shell/AppShell';

/** VB-Audio's own download page. GameDeck never ships the driver itself. */
const VB_CABLE_URL = 'https://vb-audio.com/Cable/';
const isVbCable = (e: EndpointInfo) => /vb-audio|vb-cable/i.test(`${e.name} ${e.adapter}`);

/** The graphics card noise filters, which each add a cleaned-up microphone to Windows. */
type Vendor = 'nvidia' | 'amd';
const FILTERS: Record<Vendor, { match: RegExp; device: string; url: string }> = {
  nvidia: { match: /nvidia broadcast/i, device: 'Microphone (NVIDIA Broadcast)', url: 'https://www.nvidia.com/en-us/geforce/broadcasting/broadcast-app/' },
  amd: { match: /amd streaming audio|amd noise suppression/i, device: 'Microphone (AMD Streaming Audio Device)', url: 'https://www.amd.com/en/support/download/drivers.html' },
};
const filterOf = (name: string | null | undefined) => (Object.keys(FILTERS) as Vendor[]).find((v) => !!name && FILTERS[v].match.test(name));

const VB_STEPS = ['s1', 's2', 's3', 's4', 's5', 's6'] as const;
const GD_STEPS = ['s1', 's2', 's3', 's4', 's5'] as const;
const FILTER_STEPS = ['s1', 's2', 's3', 's4', 's5'] as const;
const DC_STEPS = ['s1', 's2', 's3', 's4', 's5', 's6', 's7'] as const;
const VERIFY_STEPS = ['s1', 's2'] as const;
const TROUBLE = ['nothing', 'noVoice', 'noSounds', 'cut', 'pitch', 'crackle', 'echo', 'allPc', 'double'] as const;

/** Optional checks (the noise filter) never stand in the way of "everything is ready". */
type Check = { tone: 'ok' | 'warn' | 'bad'; text: string; action?: ReactNode; optional?: boolean };

/** Step-by-step VB-CABLE, noise filter and Discord setup, with a live check of this PC's configuration. */
export function GuideView({ go }: { go(v: ViewId): void }) {
  const { t } = useTranslation();
  const { state, updateAudio } = useDeck();
  const { routing, status, audio } = state;

  const cable = routing.cables.find((c) => isVbCable(c.render));
  const selected = routing.selected;
  // A filter already on this PC decides which card the guide opens on.
  const filterDevice: AudioDevice | undefined = state.devices.inputs.find((d) => filterOf(d.name));
  const [vendor, setVendor] = useState<Vendor>(() => filterOf(filterDevice?.name) ?? 'nvidia');
  const names = {
    render: cable?.render.name ?? 'CABLE Input (VB-Audio Virtual Cable)',
    capture: cable?.capture?.name ?? 'CABLE Output (VB-Audio Virtual Cable)',
    headphones: (status.monitor.enabled && status.monitor.deviceName) || t('guide.yourHeadphones'),
    filtered: filterDevice?.name ?? FILTERS[vendor].device,
  };

  const useCable = cable && <Button size="sm" variant="primary" onClick={() => updateAudio({ virtualDeviceId: cable.render.id })}>{t('guide.fix.useCable')}</Button>;
  const openMixer = <Button size="sm" onClick={() => go('mixer')}>{t('guide.fix.openMixer')}</Button>;

  const checks: Check[] = [];
  if (!cable) {
    checks.push({
      tone: 'bad',
      text: t('guide.checks.vbMissing'),
      action: <a className="btn btn-primary btn-sm" href={VB_CABLE_URL} target="_blank" rel="noopener noreferrer"><Download size={14} /><span>{t('guide.fix.download')}</span></a>,
    });
    // Say what the call is getting instead, e.g. a speaker endpoint picked by mistake.
    if (audio.virtualDeviceId && status.virtual.deviceName) checks.push({ tone: 'warn', text: t('guide.checks.vbNotSelected', { name: status.virtual.deviceName }) });
  } else if (selected && isVbCable(selected.render)) {
    checks.push(status.virtual.ok
      ? { tone: 'ok', text: t('guide.checks.vbSelected', { name: selected.render.name }) }
      : { tone: 'bad', text: status.virtual.error ?? t('guide.checks.virtualOff'), action: openMixer });
  } else if (!audio.virtualDeviceId) {
    checks.push({ tone: 'bad', text: t('guide.checks.virtualOff'), action: useCable });
  } else {
    checks.push({ tone: 'warn', text: t('guide.checks.vbNotSelected', { name: status.virtual.deviceName ?? selected?.render.name ?? t('common.dash') }), action: useCable });
  }
  if (selected?.capture && isVbCable(selected.render)) {
    checks.push(selected.capture.state === 'Active'
      ? { tone: 'ok', text: t('guide.checks.captureOk', { name: selected.capture.name }) }
      : { tone: 'bad', text: t('guide.checks.captureOff', { name: selected.capture.name }) });
  }
  if (!audio.micEnabled || !status.mic.enabled) {
    checks.push({
      tone: 'warn',
      text: t('guide.checks.micOff'),
      action: audio.micDeviceId ? <Button size="sm" variant="primary" onClick={() => updateAudio({ micEnabled: true })}>{t('guide.fix.mixMic')}</Button> : openMixer,
    });
  } else if (audio.micMuted) {
    checks.push({ tone: 'warn', text: t('guide.checks.micMuted'), action: <Button size="sm" variant="primary" onClick={() => updateAudio({ micMuted: false })}>{t('mixer.unmute')}</Button> });
  } else if (status.mic.ok) {
    checks.push({ tone: 'ok', text: t('guide.checks.micOk', { name: status.mic.deviceName ?? t('common.dash') }) });
  } else {
    checks.push({ tone: 'bad', text: status.mic.error ?? t('guide.checks.micOff'), action: openMixer });
  }
  if (filterOf(status.mic.deviceName)) {
    checks.push({ tone: 'ok', text: t('guide.checks.filterOk', { name: status.mic.deviceName }), optional: true });
  } else if (filterDevice) {
    checks.push({
      tone: 'warn',
      text: t('guide.checks.filterAvailable', { name: filterDevice.name }),
      action: <Button size="sm" variant="primary" onClick={() => updateAudio({ micDeviceId: filterDevice.id, micEnabled: true })}>{t('guide.fix.useFilter')}</Button>,
      optional: true,
    });
  } else {
    checks.push({ tone: 'warn', text: t('guide.checks.filterMissing'), optional: true });
  }
  checks.push(status.monitor.enabled && status.monitor.ok
    ? { tone: 'ok', text: t('guide.checks.headOk', { name: status.monitor.deviceName ?? t('common.dash') }) }
    : { tone: 'warn', text: t('guide.checks.headOff'), action: openMixer });
  const allGood = checks.every((c) => c.tone === 'ok' || c.optional);

  const steps = (prefix: string, keys: readonly string[]) => (
    <ol className="steps">
      {keys.map((k) => (
        <li key={k}><Trans i18nKey={`${prefix}.${k}`} values={names} components={{ b: <b />, code: <code /> }} /></li>
      ))}
    </ol>
  );

  return (
    <div className="page guide">
      <PageHeader title={t('guide.title')} subtitle={t('guide.subtitle')} />

      <div className="guide-stack">
        <Card eyebrow={t('guide.checkEyebrow')} title={t('guide.checkTitle')}>
          <ul className="routing-list">
            {checks.map((c, i) => {
              const Icon = c.tone === 'ok' ? CheckCircle2 : c.tone === 'warn' ? TriangleAlert : CircleAlert;
              return (
                <li key={i} className={`routing-item guide-check is-${c.tone === 'bad' ? 'error' : c.tone === 'warn' ? 'warning' : 'ok'}`}>
                  <Icon size={16} strokeWidth={1.75} aria-hidden />
                  <div><span>{c.text}</span></div>
                  {c.action && <div className="guide-check-action">{c.action}</div>}
                </li>
              );
            })}
          </ul>
          {allGood && <p className="small text-ok">{t('guide.checks.allGood')}</p>}
        </Card>

        <Card eyebrow={t('guide.part1Eyebrow')} title={t('guide.part1Title')}
          actions={<a className="btn btn-secondary btn-sm" href={VB_CABLE_URL} target="_blank" rel="noopener noreferrer"><Download size={14} /><span>{t('guide.fix.download')}</span></a>}>
          <p className="muted small guide-lead"><Trans i18nKey="guide.vbWhy" components={{ b: <b /> }} /></p>
          {steps('guide.vb', VB_STEPS)}
        </Card>

        <Card eyebrow={t('guide.part2Eyebrow')} title={t('guide.part2Title')}
          actions={<Segmented<Vendor> size="sm" label={t('guide.vendor')} value={vendor} onChange={setVendor} options={[{ value: 'nvidia', label: 'NVIDIA' }, { value: 'amd', label: 'AMD' }]} />}>
          <p className="muted small guide-lead">{t('guide.filterWhy')}</p>
          {steps(`guide.${vendor}`, FILTER_STEPS)}
          <p className="faint small">{t(vendor === 'nvidia' ? 'guide.nvidiaNote' : 'guide.amdNote')}</p>
          <div className="btn-row">
            <a className="btn btn-secondary btn-sm" href={FILTERS[vendor].url} target="_blank" rel="noopener noreferrer"><Download size={14} /><span>{t(vendor === 'nvidia' ? 'guide.nvidiaDownload' : 'guide.amdDownload')}</span></a>
          </div>
        </Card>

        <Card eyebrow={t('guide.part3Eyebrow')} title={t('guide.part3Title')} actions={openMixer}>
          {steps('guide.gd', GD_STEPS)}
        </Card>

        <RoutingCard />

        <Card eyebrow={t('guide.part4Eyebrow')} title={t('guide.part4Title')}>
          <p className="small guide-lead"><Trans i18nKey="guide.discordPath" components={{ b: <b /> }} /></p>
          {steps('guide.dc', DC_STEPS)}
          <p className="faint small">{t('guide.discordNote')}</p>
        </Card>

        <Card eyebrow={t('guide.part5Eyebrow')} title={t('guide.part5Title')}>
          {steps('guide.verify', VERIFY_STEPS)}
        </Card>

        <Card eyebrow={t('guide.troubleEyebrow')} title={t('guide.troubleTitle')} pad={false}>
          <div className="guide-faq">
            {TROUBLE.map((k) => (
              <details key={k}>
                <summary>{t(`guide.trouble.${k}.q`)}</summary>
                <p className="muted small">{t(`guide.trouble.${k}.a`)}</p>
              </details>
            ))}
          </div>
        </Card>
      </div>
    </div>
  );
}
