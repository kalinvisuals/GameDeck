import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Cpu, HardDrive, MemoryStick, MonitorCog, Thermometer } from 'lucide-react';
import type { HardwareSnapshot, SensorId } from '../../lib/types';
import { useHardware } from '../../state/hardware';
import { Card } from '../../ui/Card';
import { Skeleton } from '../../ui/extras';
import { sensorText } from './actions';

/** Live readings, pushed by the PC every 2 s while this is on screen. Missing sensors say so. */
export function HardwarePanel() {
  const { t } = useTranslation();
  const h = useHardware();

  if (!h) {
    return (
      <div className="hw-grid" aria-busy="true">
        {Array.from({ length: 4 }, (_, i) => <Skeleton key={i} height={180} radius={16} />)}
      </div>
    );
  }

  const gpuSource = h.gpu.source === 'nvml' ? t('hw.sourceNvidia') : h.gpu.source === 'windows' ? t('hw.sourceWindows') : t('hw.sourceNone');

  return (
    <>
      <div className="hw-grid">
        <Card eyebrow={t('hw.cpu')} title={h.cpu.model ?? t('hw.unavailable')} className="hw-card">
          <Reading icon={<Cpu size={15} />} label={t('deck.sensors.cpu.usage')} h={h} id="cpu.usage" />
          <Reading icon={<Thermometer size={15} />} label={t('deck.sensors.cpu.temp')} h={h} id="cpu.temp" />
          <Reading label={t('deck.sensors.cpu.clock')} h={h} id="cpu.clock" />
          <p className="faint small num">{t('hw.threads', { count: h.cpu.logicalCores })}</p>
        </Card>

        <Card eyebrow={t('hw.gpu')} title={h.gpu.model ?? t('hw.unavailable')} className="hw-card">
          <Reading icon={<MonitorCog size={15} />} label={t('deck.sensors.gpu.usage')} h={h} id="gpu.usage" />
          <Reading icon={<Thermometer size={15} />} label={t('deck.sensors.gpu.temp')} h={h} id="gpu.temp" />
          <Reading label={t('deck.sensors.gpu.vram')} h={h} id="gpu.vram" />
          <Reading label={t('deck.sensors.gpu.clock')} h={h} id="gpu.clock" />
          <Reading label={t('deck.sensors.gpu.fan')} h={h} id="gpu.fan" />
          <p className="faint small">{gpuSource}</p>
        </Card>

        <Card eyebrow={t('hw.memory')} title={h.memory ? t('hw.totalGb', { gb: (h.memory.totalMb / 1024).toFixed(1) }) : t('hw.unavailable')} className="hw-card">
          <Reading icon={<MemoryStick size={15} />} label={t('deck.sensors.ram.usage')} h={h} id="ram.usage" />
          <Row label={t('hw.used')} value={h.memory ? t('hw.gb', { gb: (h.memory.usedMb / 1024).toFixed(1) }) : null} />
          <Row label={t('hw.available')} value={h.memory ? t('hw.gb', { gb: (h.memory.availableMb / 1024).toFixed(1) }) : null} />
        </Card>

        <Card eyebrow={t('hw.storage')} title={t('hw.drives', { count: h.drives.length })} className="hw-card">
          {h.drives.length === 0 && <Row label={t('hw.storage')} value={null} />}
          {h.drives.map((d) => (
            <div key={d.name} className="hw-reading">
              <div className="hw-row">
                <span className="hw-label"><HardDrive size={15} /> {d.label ? `${d.name} ${d.label}` : d.name}</span>
                <span className="num">{t('hw.free', { free: d.freeGb.toFixed(0), total: d.totalGb.toFixed(0) })}</span>
              </div>
              <span className="hw-bar"><span style={{ transform: `scaleX(${d.usagePercent / 100})` }} /></span>
            </div>
          ))}
        </Card>
      </div>

      <Card eyebrow={t('hw.otherEyebrow')} title={t('hw.otherTitle')} className="hw-note">
        <Row label={t('hw.boardTemp')} value={null} />
        <Row label={t('hw.caseFans')} value={null} />
        <Row label={t('hw.driveTemps')} value={null} />
        <p className="faint small">{t('hw.whyUnavailable')}</p>
      </Card>
    </>
  );
}

function Reading({ icon, label, h, id }: { icon?: ReactNode; label: string; h: HardwareSnapshot; id: SensorId }) {
  const { t } = useTranslation();
  const s = sensorText(h, id);
  return (
    <div className="hw-reading">
      <div className="hw-row">
        <span className="hw-label">{icon}{label}</span>
        <span className={`num ${s.value === null ? 'faint' : 'hw-value'}`}>{s.value === null ? t('hw.unavailable') : `${s.value} ${s.unit}`}</span>
      </div>
      {s.share !== null && <span className="hw-bar"><span style={{ transform: `scaleX(${Math.min(1, s.share)})` }} /></span>}
      {s.detail && <span className="faint small num">{s.detail}</span>}
    </div>
  );
}

function Row({ label, value }: { label: string; value: string | null }) {
  const { t } = useTranslation();
  return (
    <div className="hw-row">
      <span className="hw-label">{label}</span>
      <span className={`num ${value === null ? 'faint' : ''}`}>{value ?? t('hw.unavailable')}</span>
    </div>
  );
}
