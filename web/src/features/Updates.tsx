import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ArrowUpCircle, ExternalLink, Github, Globe, Heart, Mail, Megaphone, RefreshCw, Scale, X } from 'lucide-react';
import { api } from '../lib/api';
import { locale } from '../i18n';
import type { NewsMessage } from '../lib/types';
import { useDeck } from '../state/DeckContext';
import { newsRead, useNewsRead, useUpdates } from '../state/updates';
import { Button } from '../ui/controls';
import { Card } from '../ui/Card';
import { Dialog } from '../ui/Dialog';

const CONTACT = 'contact@gamedeck.ro';
const external = { target: '_blank', rel: 'noopener noreferrer' } as const;

function NewsCard({ message, onClose }: { message: NewsMessage; onClose?: () => void }) {
  const { t } = useTranslation();
  return (
    <article className="news-card">
      <Megaphone size={16} className="news-icon" aria-hidden />
      <div className="news-text">
        <strong>{message.title}</strong>
        {message.body && <p>{message.body}</p>}
        {message.link && <a href={message.link} {...external}>{message.linkText ?? t('about.readMore')} <ExternalLink size={12} aria-hidden /></a>}
      </div>
      {onClose && <button type="button" className="news-close" aria-label={t('about.dismiss')} onClick={onClose}><X size={14} /></button>}
    </article>
  );
}

/**
 * Under the menu: a new version, the newest unread announcement and the donation link. Nothing at
 * all while there is nothing to say.
 */
export function MenuExtras({ onAbout }: { onAbout(): void }) {
  const { t } = useTranslation();
  const updates = useUpdates();
  const read = useNewsRead();
  if (!updates) return null;
  const unread = updates.news.find((n) => !read.includes(n.id));
  if (!updates.available && !unread && !updates.donateUrl) return null;

  return (
    <div className="menu-extras">
      {updates.available && (
        <button type="button" className="menu-link is-update" onClick={onAbout} title={t('about.newVersion', { version: updates.available.version })}>
          <ArrowUpCircle size={17} aria-hidden />
          <span className="menu-label">{t('about.newVersion', { version: updates.available.version })}</span>
        </button>
      )}
      {unread && <NewsCard message={unread} onClose={() => newsRead.mark(unread.id)} />}
      {updates.donateUrl && (
        <a className="menu-link is-donate" href={updates.donateUrl} {...external} title={t('about.donate')}>
          <Heart size={17} aria-hidden />
          <span className="menu-label">{t('about.donate')}</span>
        </a>
      )}
    </div>
  );
}

/** Settings → About: version, updates, announcements, license and donations. */
export function AboutSection() {
  const { t } = useTranslation();
  const { run, session } = useDeck();
  const updates = useUpdates();
  const [checking, setChecking] = useState(false);
  if (!updates) return null;

  const busy = checking || updates.state === 'checking';
  const check = async () => {
    setChecking(true);
    try {
      await run(() => api.checkUpdates());
    } finally {
      setChecking(false);
    }
  };
  const status = updates.state === 'downloading' ? t('about.downloading') : updates.state === 'installing' ? t('about.installing') : null;

  return (
    <>
      <Card eyebrow={t('about.eyebrow')} title={t('about.title')}>
        <dl className="facts">
          <div><dt>{t('about.version')}</dt><dd className="num">{updates.current}</dd></div>
          <div><dt>{t('about.checked')}</dt><dd>{updates.checkedAt ? new Date(updates.checkedAt).toLocaleString(locale()) : t('about.never')}</dd></div>
        </dl>

        {updates.available ? (
          <div className="update-box">
            <div className="update-head">
              <ArrowUpCircle size={20} aria-hidden />
              <strong>{t('about.available', { version: updates.available.version })}</strong>
            </div>
            {updates.available.notes && <p className="update-notes">{updates.available.notes}</p>}
            {status && <p className="muted" role="status">{status}</p>}
            {updates.error && <p className="update-error" role="alert">{updates.error}</p>}
            <div className="btn-row">
              {updates.canInstall && (
                <Button variant="primary" icon={<ArrowUpCircle size={16} />} disabled={!!status} onClick={() => void run(() => api.installUpdate())}>{t('about.install')}</Button>
              )}
              <a className="btn btn-ghost btn-md" href={updates.available.page} {...external}><ExternalLink size={16} /><span>{t('about.viewRelease')}</span></a>
            </div>
            <p className="faint small">{!updates.canInstall ? t('about.devBuild') : session.local ? t('about.installHint') : t('about.installOnPc')}</p>
          </div>
        ) : (
          <p className="muted">{busy ? t('about.checking') : t('about.upToDate')}</p>
        )}
        <Button variant="ghost" icon={<RefreshCw size={16} className={busy ? 'spin' : ''} />} disabled={busy} onClick={() => void check()}>{t('about.check')}</Button>
      </Card>

      {updates.news.length > 0 && (
        <Card eyebrow={t('about.newsEyebrow')} title={t('about.news')}>
          <div className="news-list">{updates.news.map((n) => <NewsCard key={n.id} message={n} />)}</div>
        </Card>
      )}

      <Card eyebrow={t('about.openEyebrow')} title={t('about.license')}>
        <p className="muted">{t('about.licenseBody')}</p>
        <div className="btn-row">
          <a className="btn btn-secondary btn-md" href={updates.source} {...external}><Github size={16} /><span>{t('about.source')}</span></a>
          <a className="btn btn-ghost btn-md" href={`${updates.source}/blob/main/LICENSE`} {...external}><Scale size={16} /><span>{t('about.readLicense')}</span></a>
          <a className="btn btn-ghost btn-md" href={updates.website} {...external}><Globe size={16} /><span>{t('about.website')}</span></a>
          <a className="btn btn-ghost btn-md" href={`mailto:${CONTACT}`} title={CONTACT}><Mail size={16} /><span>{t('about.contact')}</span></a>
        </div>
        {updates.donateUrl && (
          <div className="donate-box">
            <Heart size={20} aria-hidden />
            <div>
              <strong>{t('about.donate')}</strong>
              <p className="muted small">{t('about.donateBody')}</p>
              <p className="faint small">{t('about.donateNote')}</p>
            </div>
            <a className="btn btn-primary btn-md" href={updates.donateUrl} {...external}><span>{t('about.donateButton')}</span></a>
          </div>
        )}
      </Card>
    </>
  );
}

const SKIPPED_KEY = 'gamedeck.update.skipped';
const LATER_KEY = 'gamedeck.update.later';
const stored = (storage: () => Storage, key: string) => {
  try {
    return storage().getItem(key);
  } catch {
    return null;
  }
};
const store = (storage: () => Storage, key: string, value: string) => {
  try {
    storage().setItem(key, value);
  } catch {
    /* storage unavailable: the prompt simply comes back next time */
  }
};

/**
 * Shown once when the app opens and a newer version is out: what it is, what changed, and a one-click
 * install on the PC. "Later" waits until the app is opened again; "Skip" waits for the next version.
 */
export function UpdatePrompt() {
  const { t } = useTranslation();
  const { run, session } = useDeck();
  const updates = useUpdates();
  const [closedFor, setClosedFor] = useState<string | null>(null);
  const available = updates?.available;
  if (!updates || !available) return null;
  const version = available.version;
  const busy = updates.state === 'downloading' || updates.state === 'installing';
  if (!busy && (closedFor === version || stored(() => localStorage, SKIPPED_KEY) === version || stored(() => sessionStorage, LATER_KEY) === version)) return null;

  const later = () => {
    store(() => sessionStorage, LATER_KEY, version);
    setClosedFor(version);
  };
  const skip = () => {
    store(() => localStorage, SKIPPED_KEY, version);
    setClosedFor(version);
  };
  const canInstall = updates.canInstall;
  const status = updates.state === 'downloading' ? t('about.downloading') : updates.state === 'installing' ? t('about.installing') : null;

  return (
    <Dialog title={t('about.promptTitle')} onClose={later}
      footer={(
        <>
          <Button variant="ghost" onClick={skip} disabled={busy}>{t('about.skip')}</Button>
          <Button onClick={later} disabled={busy}>{t('about.later')}</Button>
          {canInstall
            ? <Button variant="primary" icon={<ArrowUpCircle size={16} />} disabled={busy} onClick={() => void run(() => api.installUpdate())}>{t('about.updateNow')}</Button>
            : <a className="btn btn-primary btn-md" href={available.page} {...external}><ExternalLink size={16} /><span>{t('about.viewRelease')}</span></a>}
        </>
      )}>
      <div className="update-prompt">
        <div className="update-prompt-head">
          <ArrowUpCircle size={28} aria-hidden />
          <div>
            <strong>{t('about.promptVersion', { version, current: updates.current })}</strong>
            {available.published && <span className="faint small">{t('about.published', { date: new Date(available.published).toLocaleDateString(locale()) })}</span>}
          </div>
        </div>
        {available.notes && (
          <>
            <span className="eyebrow">{t('about.whatsNew')}</span>
            <p className="update-notes">{available.notes}</p>
          </>
        )}
        {updates.donateUrl && (
          <div className="update-support">
            <Heart size={18} aria-hidden />
            <div>
              <p>{t('about.supportLine')}</p>
              <p className="faint small">{t('about.donateNote')}</p>
            </div>
            <a className="btn btn-secondary btn-sm" href={updates.donateUrl} {...external}><span>{t('about.donateButton')}</span></a>
          </div>
        )}
        {status && <p className="muted" role="status">{status}</p>}
        {updates.error && <p className="update-error" role="alert">{updates.error}</p>}
        <p className="faint small">{!canInstall ? t('about.devBuild') : session.local ? t('about.installHint') : t('about.installOnPc')}</p>
      </div>
    </Dialog>
  );
}
