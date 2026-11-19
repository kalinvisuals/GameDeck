import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Layers, Plus, Trash2 } from 'lucide-react';
import { api } from '../lib/api';
import { SoundIcon } from '../lib/icons';
import type { Profile } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { Badge, Button, IconButton } from '../ui/controls';
import { Card, EmptyState, PageHeader } from '../ui/Card';
import { formatDuration } from '../features/SoundPad';
import { playedMs } from '../features/TrimEditor';
import { SearchField } from '../ui/extras';
import type { ViewId } from '../shell/AppShell';

export function ProfilesView({ go }: { go(v: ViewId): void }) {
  const { t } = useTranslation();
  const { state, profile, run, setLayout } = useDeck();
  const [newName, setNewName] = useState('');
  const [query, setQuery] = useState('');
  const onProfile = new Set(profile.layout);
  const q = query.trim().toLowerCase();
  const library = state.sounds.filter((s) => !q || s.label.toLowerCase().includes(q) || s.category.toLowerCase().includes(q));

  return (
    <div className="page">
      <PageHeader title={t('profiles.title')} subtitle={t('profiles.subtitle')} />

      <div className="profile-grid">
        {state.profiles.map((p) => (
          <ProfileCard key={p.id} profile={p} active={p.id === profile.id} canDelete={state.profiles.length > 1} />
        ))}
        <form
          className="profile-card profile-new"
          onSubmit={(e) => {
            e.preventDefault();
            const name = newName.trim();
            if (!name) return;
            run(() => api.createProfile(name));
            setNewName('');
          }}
        >
          <span className="eyebrow">{t('profiles.newProfile')}</span>
          <input className="input" placeholder={t('profiles.placeholder')} value={newName} maxLength={40} onChange={(e) => setNewName(e.target.value)} />
          <Button type="submit" variant="primary" size="sm" icon={<Plus size={15} />} disabled={!newName.trim()}>{t('common.create')}</Button>
        </form>
      </div>

      <Card eyebrow={t('profiles.library')} title={t('profiles.soundsOn', { name: profile.name })} pad={false}
        actions={state.sounds.length > 6 && <SearchField value={query} onChange={setQuery} placeholder={t('profiles.searchLibrary')} />}>
        {state.sounds.length === 0 ? (
          <EmptyState compact icon={<Layers size={20} strokeWidth={1.5} />} title={t('profiles.noSounds')} actions={<Button variant="primary" onClick={() => go('soundboard')}>{t('profiles.goSoundboard')}</Button>}>
            {t('profiles.noSoundsBody')}
          </EmptyState>
        ) : (
          <ul className="library">
            {library.map((s) => {
              const added = onProfile.has(s.id);
              return (
                <li key={s.id} style={{ ['--tag' as string]: s.color }}>
                  <span className="library-icon"><SoundIcon name={s.icon} size={18} /></span>
                  <span className="library-name">{s.label}</span>
                  <span className="faint small num">{formatDuration(playedMs(s))}</span>
                  {added ? (
                    <Button
                      size="sm"
                      variant="ghost"
                      icon={<Check size={14} />}
                      onClick={() => setLayout(profile.id, profile.layout.filter((id) => id !== s.id))}
                      title={t('profiles.removeFromProfile')}
                    >
                      {t('common.added')}
                    </Button>
                  ) : (
                    <Button size="sm" icon={<Plus size={14} />} onClick={() => setLayout(profile.id, [...profile.layout, s.id])}>{t('common.add')}</Button>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </Card>
    </div>
  );
}

function ProfileCard({ profile, active, canDelete }: { profile: Profile; active: boolean; canDelete: boolean }) {
  const { t } = useTranslation();
  const { run, soundsById, activateProfile } = useDeck();
  const preview = profile.layout.slice(0, 5).map((id) => soundsById.get(id)).filter((s) => s !== undefined);
  const [name, setName] = useState(profile.name);
  const commit = () => {
    const v = name.trim();
    if (v && v !== profile.name) run(() => api.updateProfile(profile.id, { name: v }));
    else setName(profile.name);
  };
  return (
    <div className={`profile-card ${active ? 'is-active' : ''}`}>
      <div className="profile-card-top">
        {active ? <Badge tone="accent">{t('common.active')}</Badge> : <span className="faint small num">{t('profiles.buttons', { count: profile.layout.length })}</span>}
        <IconButton
          label={t('profiles.deleteProfile', { name: profile.name })}
          icon={<Trash2 size={16} />}
          disabled={!canDelete}
          onClick={() => confirm(t('profiles.confirmDelete', { name: profile.name })) && run(() => api.deleteProfile(profile.id))}
        />
      </div>
      <input className="input input-title" aria-label={t('profiles.name')} value={name} maxLength={40} onChange={(e) => setName(e.target.value)} onBlur={commit} onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()} />
      <div className="profile-preview" aria-hidden>
        {preview.length === 0 ? <span className="faint small">{t('profiles.empty')}</span> : preview.map((s) => (
          <span key={s.id} className="profile-preview-key" style={{ ['--tag' as string]: s.color }}><SoundIcon name={s.icon} size={14} /></span>
        ))}
        {profile.layout.length > preview.length && <span className="profile-preview-more num">+{profile.layout.length - preview.length}</span>}
      </div>
      {active ? (
        <span className="faint small num">{t('profiles.buttons', { count: profile.layout.length })}</span>
      ) : (
        <Button size="sm" onClick={() => activateProfile(profile.id)}>{t('profiles.switchTo')}</Button>
      )}
    </div>
  );
}
