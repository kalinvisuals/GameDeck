import { useEffect, useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import i18n from 'i18next';
import { Activity, BookOpen, Cable, Info, Laptop, LogOut, Monitor, Network, Palette, RefreshCw, ShieldCheck, Smartphone, SlidersHorizontal } from 'lucide-react';
import { api } from '../lib/api';
import { locale } from '../i18n';
import { useDeck } from '../state/DeckContext';
import { usePrefs } from '../state/prefs';
import { Badge, Button, Field, Segmented, Switch } from '../ui/controls';
import { Card, PageHeader } from '../ui/Card';
import { DeviceSelect, RoutingCard, isCable } from '../features/audio';
import { LanguageSwitch } from '../features/LanguageSwitch';
import { LogoPicker } from '../features/LogoPicker';
import { AboutSection } from '../features/Updates';
import type { ViewId } from '../shell/AppShell';

const SECTIONS = [
  { id: 'devices', icon: SlidersHorizontal },
  { id: 'voice', icon: Cable },
  { id: 'network', icon: Network },
  { id: 'prefs', icon: Laptop },
  { id: 'logo', icon: Palette },
  { id: 'diag', icon: Activity },
  { id: 'about', icon: Info },
] as const;
type SectionId = (typeof SECTIONS)[number]['id'];

/** Lets other screens open Settings on a given section, such as "About" for a new version. */
let requestedSection: SectionId | null = null;
export function openSettingsSection(id: SectionId) {
  requestedSection = id;
  window.dispatchEvent(new Event('gamedeck:settings-section'));
}

const relative = (iso: string) => {
  const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (mins < 2) return i18n.t('settings.justNow');
  if (mins < 60) return i18n.t('settings.minAgo', { count: mins });
  if (mins < 60 * 24) return i18n.t('settings.hoursAgo', { count: Math.round(mins / 60) });
  return new Date(iso).toLocaleDateString(locale());
};

const STEPS = ['s1', 's2', 's3', 's4', 's6'] as const;

/** Start with Windows. A setting of the PC itself, so it only appears in a browser on the PC. */
function AutoStartField() {
  const { t } = useTranslation();
  const { run } = useDeck();
  const [enabled, setEnabled] = useState<boolean | null>(null);

  useEffect(() => {
    void run(async () => setEnabled((await api.autoStart()).enabled));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (enabled === null) return null;

  const change = (next: boolean) => {
    setEnabled(next);
    void run(async () => {
      try {
        setEnabled((await api.setAutoStart(next)).enabled);
      } catch (e) {
        setEnabled(!next);
        throw e;
      }
    });
  };

  return (
    <Field label={t('settings.autoStart')} hint={t('settings.autoStartHint')} inline>
      <Switch checked={enabled} label={t('settings.autoStart')} onChange={change} />
    </Field>
  );
}

export function SettingsView({ go }: { go(v: ViewId): void }) {
  const { t } = useTranslation();
  const { state, session, run, signOut, updateAudio } = useDeck();
  const [prefs, setPrefs] = usePrefs();
  const [section, setSection] = useState<SectionId>(() => {
    const requested = requestedSection ?? 'devices';
    requestedSection = null;
    return requested;
  });
  useEffect(() => {
    const onRequest = () => {
      if (requestedSection) setSection(requestedSection);
      requestedSection = null;
    };
    window.addEventListener('gamedeck:settings-section', onRequest);
    return () => window.removeEventListener('gamedeck:settings-section', onRequest);
  }, []);
  const { audio, devices } = state;
  const update = updateAudio;
  const capture = state.routing.selected?.capture?.name ?? 'CABLE Output (VB-Audio Virtual Cable)';

  return (
    <div className="page settings">
      <PageHeader title={t('settings.title')} subtitle={t('settings.subtitle')} />

      <div className="settings-layout">
        <nav className="settings-nav" aria-label={t('settings.sections')}>
          {SECTIONS.map(({ id, icon: Icon }) => (
            <button key={id} className={`settings-nav-item ${section === id ? 'is-active' : ''}`} aria-current={section === id ? 'true' : undefined} onClick={() => setSection(id)}>
              <Icon size={16} strokeWidth={1.75} />
              <span>{t(`settings.section.${id}`)}</span>
            </button>
          ))}
        </nav>

        <div className="settings-body">
          {section === 'devices' && (
            <Card eyebrow={t('settings.audioEyebrow')} title={t('settings.devices')}>
              <DeviceSelect label={t('settings.headphones')} hint={t('settings.headphonesHint')} devices={devices.outputs.filter((d) => !isCable(d))} value={audio.monitorDeviceId} empty={t('common.windowsDefault')} onChange={(id) => update({ monitorDeviceId: id })} />
              <DeviceSelect label={t('settings.virtual')} hint={t('settings.virtualHint')} devices={devices.outputs.filter((d) => d.id !== audio.monitorDeviceId)} value={audio.virtualDeviceId} empty={t('common.off')} suggest={isCable} onChange={(id) => update({ virtualDeviceId: id })} />
              <DeviceSelect label={t('settings.microphone')} hint={t('settings.microphoneHint')} devices={devices.inputs.filter((d) => !isCable(d))} value={audio.micDeviceId} empty={t('common.none')} onChange={(id) => update({ micDeviceId: id })} />
              <Field label={t('settings.mixMic')} hint={t('settings.mixMicHint')} inline>
                <Switch checked={audio.micEnabled} label={t('settings.mixMic')} onChange={(v) => update({ micEnabled: v })} />
              </Field>
              <Field label={t('settings.monitor')} inline>
                <Switch checked={audio.monitorEnabled} label={t('settings.monitor')} onChange={(v) => update({ monitorEnabled: v })} />
              </Field>
              <div className="btn-row">
                <Button size="sm" icon={<RefreshCw size={15} />} onClick={() => run(() => api.refreshDevices())}>{t('common.rescan')}</Button>
              </div>
            </Card>
          )}

          {section === 'voice' && (
            <>
              <RoutingCard />
              <Card eyebrow={t('settings.setupEyebrow')} title={t('settings.setupTitle')}
                actions={<Button size="sm" variant="primary" icon={<BookOpen size={15} />} onClick={() => go('guide')}>{t('settings.openGuide')}</Button>}>
                <ol className="steps">
                  {STEPS.map((s) => (
                    <li key={s}>
                      <Trans i18nKey={`settings.steps.${s}`} values={{ capture }} components={{ b: <b />, code: <code /> }} />
                    </li>
                  ))}
                </ol>
              </Card>
            </>
          )}

          {section === 'network' && (
            <>
              <Card eyebrow={t('settings.networkEyebrow')} title={t('settings.openElsewhere')}>
                <p className="muted small">{t('settings.openElsewhereBody')}</p>
                <div className="url-list">
                  {state.server.urls.length > 0 ? state.server.urls.map((u) => <code key={u} className="url">{u}</code>) : <p className="faint small">{t('settings.noLan')}</p>}
                </div>
                <div className="privacy">
                  <ShieldCheck size={18} strokeWidth={1.6} className="accent" />
                  <p className="muted small">{t('settings.privacy')}</p>
                </div>
              </Card>
              <Card eyebrow={t('settings.accessEyebrow')} title={t('settings.paired')} pad={false}>
                <ul className="rows">
                  {state.pairedDevices.map((d) => {
                    const isThis = d.id === session.deviceId;
                    const Icon = /pc|windows|mac|browser|navigator/i.test(d.name) ? Monitor : Smartphone;
                    return (
                      <li key={d.id}>
                        <Icon size={18} strokeWidth={1.6} className="faint" />
                        <div className="rows-main">
                          <span>{d.name} {isThis && <Badge tone="accent">{t('settings.thisDevice')}</Badge>}</span>
                          <span className="faint small">{t('settings.pairedLine', { date: new Date(d.createdUtc).toLocaleDateString(locale()), ago: relative(d.lastSeenUtc) })}</span>
                        </div>
                        {!isThis && <Button size="sm" variant="ghost" onClick={() => confirm(t('settings.confirmUnpair', { name: d.name })) && run(() => api.revokeDevice(d.id))}>{t('settings.unpair')}</Button>}
                      </li>
                    );
                  })}
                </ul>
              </Card>
              <Button variant="danger" block icon={<LogOut size={16} />} onClick={() => run(async () => { await api.logout(); signOut(); })}>
                {t('settings.unpairThis')}
              </Button>
            </>
          )}

          {section === 'prefs' && (
            <Card eyebrow={t('settings.prefsEyebrow')} title={t('settings.prefs')}>
              {session.local && <AutoStartField />}
              <Field label={t('settings.language')} hint={t('settings.languageHint')} inline>
                <LanguageSwitch />
              </Field>
              <Field label={t('settings.padSize')} inline>
                <Segmented size="sm" label={t('settings.padSize')} value={prefs.density} onChange={(density) => setPrefs({ density })}
                  options={[{ value: 'compact', label: t('settings.sizes.compact') }, { value: 'regular', label: t('settings.sizes.regular') }, { value: 'large', label: t('settings.sizes.large') }]} />
              </Field>
              <Field label={t('settings.durations')} hint={t('settings.durationsHint')} inline>
                <Switch checked={prefs.showDurations} label={t('settings.durations')} onChange={(v) => setPrefs({ showDurations: v })} />
              </Field>
              <Field label={t('settings.reduceMotion')} hint={t('settings.reduceMotionHint')} inline>
                <Switch checked={prefs.reduceMotion} label={t('settings.reduceMotion')} onChange={(v) => setPrefs({ reduceMotion: v })} />
              </Field>
              <Field label={t('settings.deckAnimations')} hint={t('settings.deckAnimationsHint')} inline>
                <Switch checked={prefs.deckAnimations} label={t('settings.deckAnimations')} onChange={(v) => setPrefs({ deckAnimations: v })} />
              </Field>
            </Card>
          )}

          {section === 'logo' && <LogoPicker />}

          {section === 'about' && <AboutSection />}

          {section === 'diag' && (
            <Card eyebrow={t('settings.diagEyebrow')} title={t('settings.system')}>
              <dl className="facts facts-wide">
                <div><dt>GameDeck</dt><dd className="num">{state.server.version}</dd></div>
                <div><dt>{t('settings.started')}</dt><dd>{new Date(state.diagnostics.startedUtc).toLocaleString(locale())}</dd></div>
                <div><dt>{t('settings.micBuffer')}</dt><dd className="num">{t('settings.micBufferValue', { underruns: state.diagnostics.mic.underruns, drops: state.diagnostics.mic.drops, ms: state.diagnostics.mic.bufferedMs.toFixed(0) })}</dd></div>
                <div><dt>{t('settings.outputBuffer')}</dt><dd className="num">{t('dashboard.ms', { value: audio.latencyMs })}</dd></div>
                <div><dt>{t('settings.headphones')}</dt><dd>{state.status.monitor.deviceName ?? t('common.dash')}</dd></div>
                <div><dt>{t('settings.virtualOutput')}</dt><dd>{state.status.virtual.deviceName ?? t('common.off')}</dd></div>
                <div><dt>{t('settings.microphone')}</dt><dd>{state.status.mic.deviceName ?? t('common.off')}</dd></div>
                <div><dt>{t('settings.windows')}</dt><dd>{state.diagnostics.os}</dd></div>
                <div><dt>{t('settings.runtime')}</dt><dd>{state.diagnostics.runtime}</dd></div>
                <div><dt>{t('settings.dataFolder')}</dt><dd><code>{state.diagnostics.dataDirectory}</code></dd></div>
                <div><dt>{t('settings.port')}</dt><dd className="num">{state.server.port}</dd></div>
              </dl>
              <p className="faint small"><Trans i18nKey="settings.logs" components={{ code: <code /> }} /></p>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}
