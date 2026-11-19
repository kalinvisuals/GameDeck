import {
  AlarmClock, Bell, Bird, Bomb, Cat, Clapperboard, Crosshair, Crown, Dices, Dog, Drum, Flame, Frown,
  Gamepad2, Ghost, Guitar, Hand, Headphones, Heart, Laugh, Megaphone, Mic, Music, PartyPopper, Piano,
  Radio, Rocket, Siren, Skull, Smile, Sparkles, Star, Swords, Target, ThumbsDown, ThumbsUp, Trophy,
  Volume2, Waves, Zap, type LucideIcon,
} from 'lucide-react';

/** Curated button icons. Keys are what gets stored in config, so never rename one. */
export const SOUND_ICONS: Record<string, LucideIcon> = {
  music: Music,
  volume: Volume2,
  mic: Mic,
  megaphone: Megaphone,
  bell: Bell,
  siren: Siren,
  alarm: AlarmClock,
  laugh: Laugh,
  smile: Smile,
  frown: Frown,
  clap: Hand,
  party: PartyPopper,
  heart: Heart,
  'thumbs-up': ThumbsUp,
  'thumbs-down': ThumbsDown,
  star: Star,
  sparkles: Sparkles,
  trophy: Trophy,
  crown: Crown,
  skull: Skull,
  ghost: Ghost,
  flame: Flame,
  zap: Zap,
  bomb: Bomb,
  rocket: Rocket,
  swords: Swords,
  crosshair: Crosshair,
  target: Target,
  gamepad: Gamepad2,
  dice: Dices,
  drum: Drum,
  guitar: Guitar,
  piano: Piano,
  headphones: Headphones,
  radio: Radio,
  waves: Waves,
  film: Clapperboard,
  cat: Cat,
  dog: Dog,
  bird: Bird,
};

export function SoundIcon({ name, size = 26 }: { name: string; size?: number }) {
  const Icon = SOUND_ICONS[name] ?? Music;
  return <Icon size={size} strokeWidth={1.6} aria-hidden />;
}

/** Identity tags: a small marker on each button, never a fill. Muted so the grid stays calm. */
export const TAG_COLORS = [
  '#a9d6ff', '#c9d1dc', '#8fb8c9', '#a9b8a1', '#d6c3a1', '#e0b48f',
  '#c9a4a4', '#b3a9d6', '#7d8696', '#f4f5f7',
];
