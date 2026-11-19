import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Copy, Download, Plus, Trash2, Upload } from 'lucide-react';
import { api } from '../../lib/api';
import type { DeckProfile } from '../../lib/types';
import { useDeck } from '../../state/DeckContext';
import { Badge, Button, IconButton } from '../../ui/controls';

const MAX_IMPORT_BYTES = 30 * 1024 * 1024;

/** Smart Deck layouts ("Gaming", "Streaming"). Separate from the soundboard's profiles. */
export function DeckProfilesPanel() {
  const { t } = useTranslation();
  const { state, run, toast } = useDeck();
  const { profiles, activeProfileId } = state.deck;
  const [newName, setNewName] = useState('');
  const file = useRef<HTMLInputElement>(null);

  const importFile = (f: File | undefined) => {
    if (!f) return;
    if (f.size > MAX_IMPORT_BYTES) return toast(t('deck.importTooBig'), 'error');
    run(async () => {
      let parsed: unknown;
      try {
        parsed = JSON.parse(await f.text());
      } catch {
        throw new Error(t('deck.importNotJson'));
      }
      const p = await api.importDeck(parsed);
      toast(t('deck.imported', { name: p.name }), 'success');
    });
  };

  return (
    <div>
      <div className="deck-toolbar">
        <p className="faint small">{t('deckProfiles.intro')}</p>
        <Button size="sm" icon={<Upload size={15} />} onClick={() => file.current?.click()}>{t('deckProfiles.import')}</Button>
        <input ref={file} type="file" hidden accept=".json,application/json" onChange={(e) => { importFile(e.target.files?.[0]); e.target.value = ''; }} />
      </div>

      <div className="profile-grid">
        {profiles.map((p) => <ProfileCard key={p.id} profile={p} active={p.id === activeProfileId} canDelete={profiles.length > 1} />)}
        <form
          className="profile-card profile-new"
          onSubmit={(e) => {
            e.preventDefault();
            if (!newName.trim()) return;
            run(() => api.createDeckProfile(newName.trim()));
            setNewName('');
          }}
        >
          <span className="eyebrow">{t('deckProfiles.new')}</span>
          <input className="input" placeholder={t('deckProfiles.placeholder')} value={newName} maxLength={40} onChange={(e) => setNewName(e.target.value)} />
          <Button type="submit" variant="primary" size="sm" icon={<Plus size={15} />} disabled={!newName.trim()}>{t('common.create')}</Button>
        </form>
      </div>
    </div>
  );
}

function ProfileCard({ profile, active, canDelete }: { profile: DeckProfile; active: boolean; canDelete: boolean }) {
  const { t } = useTranslation();
  const { run } = useDeck();
  const [name, setName] = useState(profile.name);
  const buttons = profile.pages.reduce((n, p) => n + p.buttons.length, 0);

  const commit = () => {
    const v = name.trim();
    if (v && v !== profile.name) run(() => api.saveDeckProfile({ ...profile, name: v }));
    else setName(profile.name);
  };

  const exportFile = () =>
    run(async () => {
      const data = await api.exportDeckProfile(profile.id);
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = `${profile.name.replace(/[^\p{L}\p{N} _-]+/gu, '').trim() || 'deck'}.gamedeck-deck.json`;
      a.click();
      URL.revokeObjectURL(url);
    });

  return (
    <div className={`profile-card ${active ? 'is-active' : ''}`}>
      <div className="profile-card-top">
        {active ? <Badge tone="accent">{t('common.active')}</Badge> : <span className="faint small num">{t('deckProfiles.grid', { rows: profile.rows, columns: profile.columns })}</span>}
        <IconButton label={t('deckProfiles.delete', { name: profile.name })} icon={<Trash2 size={16} />} disabled={!canDelete}
          onClick={() => confirm(t('deckProfiles.confirmDelete', { name: profile.name })) && run(() => api.deleteDeckProfile(profile.id))} />
      </div>
      <input className="input input-title" aria-label={t('deckProfiles.name')} value={name} maxLength={40}
        onChange={(e) => setName(e.target.value)} onBlur={commit} onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()} />
      <span className="faint small num">{t('deckProfiles.summary', { buttons, pages: profile.pages.length })}</span>
      <div className="scene-actions">
        {active ? <span /> : <Button size="sm" onClick={() => run(() => api.activateDeckProfile(profile.id))}>{t('profiles.switchTo')}</Button>}
        <IconButton label={t('deckProfiles.duplicate')} icon={<Copy size={16} />} onClick={() => run(() => api.createDeckProfile('', profile.id))} />
        <IconButton label={t('deckProfiles.export')} icon={<Download size={16} />} onClick={exportFile} />
      </div>
    </div>
  );
}
