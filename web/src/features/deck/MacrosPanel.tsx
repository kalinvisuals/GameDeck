import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowDown, ArrowUp, Check, ListOrdered, Pencil, Play, Plus, Square, Trash2, X } from 'lucide-react';
import { api } from '../../lib/api';
import type { DeckAction, DeckMacro } from '../../lib/types';
import { useDeck } from '../../state/DeckContext';
import { Badge, Button, Field, IconButton } from '../../ui/controls';
import { EmptyState } from '../../ui/Card';
import { Dialog } from '../../ui/Dialog';
import { ActionFields } from './ActionFields';
import { describeAction, defaultAction } from './actions';

const MAX_STEPS = 32;

export function MacrosPanel() {
  const { t } = useTranslation();
  const { state, run } = useDeck();
  const { macros, runningMacros, macroErrors } = state.deck;
  const [editing, setEditing] = useState<DeckMacro | null>(null);

  return (
    <div className="macros">
      <div className="deck-toolbar">
        <p className="faint small">{t('macros.intro')}</p>
        <Button size="sm" variant="primary" icon={<Plus size={15} />} onClick={() => setEditing({ id: '', name: '', steps: [] })}>{t('macros.new')}</Button>
      </div>

      {macros.length === 0 && (
        <EmptyState compact icon={<ListOrdered size={20} strokeWidth={1.5} />} title={t('macros.empty')}>
          {t('macros.emptyBody')}
        </EmptyState>
      )}

      <div className="profile-grid">
        {macros.map((m) => {
          const running = runningMacros.includes(m.id);
          const error = macroErrors[m.id];
          return (
            <div key={m.id} className={`profile-card macro-card ${running ? 'is-active' : ''}`}>
              <div className="profile-card-top">
                <span className="faint small num">{t('macros.steps', { count: m.steps.length })}</span>
                {running && <Badge tone="accent">{t('deck.running')}</Badge>}
              </div>
              <strong className="macro-name">{m.name}</strong>
              <ol className="macro-preview small faint">
                {m.steps.slice(0, 3).map((s, i) => <li key={i}>{describeAction(s, t, state)}</li>)}
                {m.steps.length > 3 && <li className="num">{t('macros.more', { count: m.steps.length - 3 })}</li>}
              </ol>
              {error && <p className="small text-danger">{error}</p>}
              <div className="scene-actions">
                {running ? (
                  <Button size="sm" variant="danger" icon={<Square size={11} fill="currentColor" strokeWidth={0} />} onClick={() => run(() => api.stopMacro(m.id))}>{t('macros.stop')}</Button>
                ) : (
                  <Button size="sm" variant="primary" icon={<Play size={14} />} disabled={m.steps.length === 0} onClick={() => run(() => api.runMacro(m.id))}>{t('macros.run')}</Button>
                )}
                <IconButton label={t('macros.edit')} icon={<Pencil size={16} />} onClick={() => setEditing(m)} />
                <IconButton label={t('macros.delete')} icon={<Trash2 size={16} />} onClick={() => confirm(t('macros.confirmDelete', { name: m.name })) && run(() => api.deleteMacro(m.id))} />
              </div>
            </div>
          );
        })}
      </div>

      {editing && <MacroEditor macro={editing} onClose={() => setEditing(null)} />}
    </div>
  );
}

/** Steps run top to bottom; the macro stops at the first step that fails. Saved with "Done". */
function MacroEditor({ macro, onClose }: { macro: DeckMacro; onClose(): void }) {
  const { t } = useTranslation();
  const { state } = useDeck();
  const [name, setName] = useState(macro.name);
  const [steps, setSteps] = useState<DeckAction[]>(macro.steps);
  const [open, setOpen] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const change = (next: DeckAction[]) => {
    setSteps(next);
    setError(null);
  };
  const swap = (i: number, j: number) => {
    const next = [...steps];
    [next[i], next[j]] = [next[j], next[i]];
    change(next);
    setOpen(j);
  };

  const done = async () => {
    setSaving(true);
    try {
      const body = { name: name.trim() || t('macros.untitled'), steps };
      if (macro.id) await api.saveMacro({ id: macro.id, ...body });
      else await api.createMacro(body);
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Dialog
      title={macro.id ? t('macros.editTitle') : t('macros.newTitle')}
      onClose={onClose}
      footer={<Button variant="primary" icon={<Check size={16} />} disabled={saving} onClick={done}>{t('common.done')}</Button>}
    >
      <Field label={t('macros.name')}>
        <input className="input" value={name} maxLength={40} placeholder={t('macros.namePlaceholder')} onChange={(e) => setName(e.target.value)} />
      </Field>

      <Field label={t('macros.stepsTitle')} hint={t('macros.stepsHint')}>
        {steps.length === 0 && <p className="faint small">{t('macros.noSteps')}</p>}
        <ol className="macro-steps">
          {steps.map((s, i) => (
            <li key={i} className={`macro-step ${open === i ? 'is-open' : ''}`}>
              <div className="macro-step-head">
                <span className="macro-step-n num">{i + 1}</span>
                <button type="button" className="macro-step-title" onClick={() => setOpen(open === i ? null : i)}>{describeAction(s, t, state)}</button>
                <IconButton label={t('macros.up')} icon={<ArrowUp size={15} />} disabled={i === 0} onClick={() => swap(i, i - 1)} />
                <IconButton label={t('macros.down')} icon={<ArrowDown size={15} />} disabled={i === steps.length - 1} onClick={() => swap(i, i + 1)} />
                <IconButton label={t('macros.removeStep')} icon={<X size={15} />} onClick={() => { change(steps.filter((_, j) => j !== i)); setOpen(null); }} />
              </div>
              {open === i && <ActionFields inMacro action={s} onChange={(a) => change(steps.map((x, j) => (j === i ? a : x)))} />}
            </li>
          ))}
        </ol>
        <Button size="sm" icon={<Plus size={15} />} disabled={steps.length >= MAX_STEPS} onClick={() => {
          change([...steps, defaultAction('wait', state)]);
          setOpen(steps.length);
        }}>
          {t('macros.addStep')}
        </Button>
      </Field>

      {error && <p className="small text-danger" role="alert">{error}</p>}
    </Dialog>
  );
}
