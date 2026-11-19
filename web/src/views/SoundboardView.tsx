import { useCallback, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AudioLines, Check, Maximize2, Pencil, Plus, SearchX, Star, Upload } from 'lucide-react';
import { useDeck } from '../state/DeckContext';
import { usePrefs } from '../state/prefs';
import { Button, IconButton, Segmented } from '../ui/controls';
import { EmptyState } from '../ui/Card';
import { Chip, SearchField } from '../ui/extras';
import { SoundPad } from '../features/SoundPad';
import { EditDialog } from '../features/EditDialog';
import { useReorder } from '../features/useReorder';

const DENSITY_PX = { compact: 112, regular: 140, large: 180 } as const;
type Filter = { kind: 'all' } | { kind: 'favorites' } | { kind: 'category'; name: string };

export function SoundboardView({ focus, onFocus }: { focus: boolean; onFocus(): void }) {
  const { t } = useTranslation();
  const { state, profile, soundsById, playingBySound, trigger, upload, activateProfile, setLayout } = useDeck();
  const [prefs, setPrefs] = usePrefs();
  const [editMode, setEditMode] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [dropping, setDropping] = useState(false);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<Filter>({ kind: 'all' });
  const fileInput = useRef<HTMLInputElement>(null);

  const filtering = query.trim() !== '' || filter.kind !== 'all';
  const commit = useCallback((layout: string[]) => setLayout(profile.id, layout), [setLayout, profile.id]);
  const { order, draggingId, onPointerDown, consumeClick } = useReorder(profile.layout, editMode && !filtering, commit);

  const onProfile = useMemo(() => order.map((id) => soundsById.get(id)).filter((s) => s !== undefined), [order, soundsById]);
  const categories = useMemo(() => {
    const m = new Map<string, number>();
    onProfile.forEach((s) => s.category && m.set(s.category, (m.get(s.category) ?? 0) + 1));
    return [...m.entries()].sort((a, b) => a[0].localeCompare(b[0]));
  }, [onProfile]);
  const favoriteCount = onProfile.filter((s) => s.favorite).length;

  const pads = useMemo(() => {
    const q = query.trim().toLowerCase();
    return onProfile.filter((s) => {
      if (filter.kind === 'favorites' && !s.favorite) return false;
      if (filter.kind === 'category' && s.category !== filter.name) return false;
      return !q || s.label.toLowerCase().includes(q) || s.category.toLowerCase().includes(q) || s.originalFileName.toLowerCase().includes(q);
    });
  }, [onProfile, filter, query]);

  const editing = editingId ? soundsById.get(editingId) : undefined;
  const showFilters = !focus && (categories.length > 0 || favoriteCount > 0);

  return (
    <div
      className={`board ${dropping ? 'is-dropping' : ''}`}
      style={{ ['--tile-user' as string]: `${DENSITY_PX[prefs.density]}px` }}
      data-density={prefs.density}
      onDragOver={(e) => {
        if (e.dataTransfer.types.includes('Files')) {
          e.preventDefault();
          setDropping(true);
        }
      }}
      onDragLeave={(e) => e.currentTarget === e.target && setDropping(false)}
      onDrop={(e) => {
        e.preventDefault();
        setDropping(false);
        upload(Array.from(e.dataTransfer.files));
      }}
    >
      {!focus && (
        <header className="board-head">
          <div className="board-title">
            <span className="eyebrow">{t('soundboard.eyebrow')}</span>
            <div className="profile-switch" role="tablist" aria-label={t('soundboard.profiles')}>
              {state.profiles.map((p) => (
                <button
                  key={p.id}
                  role="tab"
                  aria-selected={p.id === profile.id}
                  className={`profile-tab ${p.id === profile.id ? 'is-active' : ''}`}
                  onClick={() => p.id !== profile.id && activateProfile(p.id)}
                >
                  {p.name}
                </button>
              ))}
            </div>
          </div>
          <div className="board-actions">
            <div className="only-wide">
              <Segmented
                size="sm"
                label={t('soundboard.padSize')}
                value={prefs.density}
                onChange={(density) => setPrefs({ density })}
                options={[
                  { value: 'compact', label: t('soundboard.sizeS') },
                  { value: 'regular', label: t('soundboard.sizeM') },
                  { value: 'large', label: t('soundboard.sizeL') },
                ]}
              />
            </div>
            <IconButton label={t('soundboard.import')} icon={<Upload size={18} />} onClick={() => fileInput.current?.click()} />
            <Button size="sm" variant={editMode ? 'primary' : 'secondary'} icon={editMode ? <Check size={15} /> : <Pencil size={15} />} onClick={() => setEditMode((v) => !v)}>
              {editMode ? t('common.done') : t('common.edit')}
            </Button>
            <IconButton className="only-wide-inline" label={t('soundboard.focus')} icon={<Maximize2 size={18} />} onClick={onFocus} />
          </div>
        </header>
      )}

      {!focus && onProfile.length > 0 && (
        <div className="board-filters">
          <SearchField value={query} onChange={setQuery} placeholder={t('soundboard.search', { count: onProfile.length })} />
          {showFilters && (
            <div className="chips" aria-label={t('soundboard.filter')}>
              <Chip active={filter.kind === 'all'} onClick={() => setFilter({ kind: 'all' })}>{t('common.all')}</Chip>
              {favoriteCount > 0 && (
                <Chip active={filter.kind === 'favorites'} onClick={() => setFilter({ kind: 'favorites' })} icon={<Star size={13} strokeWidth={2} />} count={favoriteCount}>
                  {t('soundboard.favorites')}
                </Chip>
              )}
              {categories.map(([name, count]) => (
                <Chip key={name} active={filter.kind === 'category' && filter.name === name} onClick={() => setFilter({ kind: 'category', name })} count={count}>
                  {name}
                </Chip>
              ))}
            </div>
          )}
        </div>
      )}

      {editMode && !focus && (
        <p className="board-hint faint small">
          {filtering
            ? t('soundboard.hintFiltered')
            : t('soundboard.hintReorder')}
        </p>
      )}

      {onProfile.length === 0 && !editMode ? (
        <EmptyState
          icon={<AudioLines size={26} strokeWidth={1.5} />}
          title={t('soundboard.empty', { name: profile.name })}
          actions={
            <>
              <Button variant="primary" icon={<Plus size={16} />} onClick={() => fileInput.current?.click()}>
                {t('soundboard.import')}
              </Button>
              {state.sounds.length > 0 && <span className="faint small">{t('soundboard.orLibrary')}</span>}
            </>
          }
        >
          {t('soundboard.emptyBody')}
        </EmptyState>
      ) : pads.length === 0 && !editMode ? (
        <EmptyState compact icon={<SearchX size={20} strokeWidth={1.5} />} title={t('soundboard.noMatch')} actions={<Button size="sm" onClick={() => { setQuery(''); setFilter({ kind: 'all' }); }}>{t('soundboard.clearFilters')}</Button>}>
          {query ? t('soundboard.noMatchQuery', { profile: profile.name, query }) : t('soundboard.noMatchFilter', { profile: profile.name })}
        </EmptyState>
      ) : (
        <div className="pad-grid">
          {pads.map((s) => (
            <SoundPad
              key={s.id}
              sound={s}
              playing={playingBySound.get(s.id)}
              editMode={editMode && !focus}
              reorderable={!filtering}
              showDuration={prefs.showDurations}
              dragging={draggingId === s.id}
              onPointerDown={(e) => onPointerDown(s.id, e)}
              onActivate={() => {
                if (consumeClick()) return;
                if (editMode && !focus) setEditingId(s.id);
                else trigger(s.id);
              }}
            />
          ))}
          {editMode && !focus && (
            <button className="pad pad-add" onClick={() => fileInput.current?.click()}>
              <span className="pad-icon">
                <Plus size={24} strokeWidth={1.5} />
              </span>
              <span className="pad-label">{t('soundboard.addSounds')}</span>
              <span className="pad-sub">{t('soundboard.formats')}</span>
            </button>
          )}
        </div>
      )}

      <input
        ref={fileInput}
        type="file"
        accept=".mp3,.wav,audio/mpeg,audio/wav,audio/x-wav"
        multiple
        hidden
        onChange={(e) => {
          upload(Array.from(e.target.files ?? []));
          e.target.value = '';
        }}
      />

      {editing && (
        <EditDialog
          sound={editing}
          profile={profile}
          categories={[...new Set(state.sounds.map((s) => s.category).filter(Boolean))].sort()}
          onClose={() => setEditingId(null)}
        />
      )}
    </div>
  );
}
