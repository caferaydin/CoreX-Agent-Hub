import type { ReactNode } from 'react';

/**
 * One icon family, drawn here.
 *
 * A package was left out on purpose: this UI needs six glyphs, and a dependency for six glyphs
 * is a dependency to keep current, audit and bundle. Drawing them keeps the set coherent by
 * construction - one stroke width, one grid, one visual weight - which is the thing an icon
 * pack is usually bought for.
 *
 * Icons never replace a label. Every one of these sits beside text or carries an accessible
 * name, because a glyph alone is a guess.
 */
export type IconName = 'tasks' | 'repositories' | 'agents' | 'hub' | 'chevron' | 'external';

const paths: Record<IconName, ReactNode> = {
  // A checklist: the working surface.
  tasks: (
    <>
      <path d="M3 4.5 4.4 6 7 3" />
      <path d="M3 9.5 4.4 11 7 8" />
      <path d="M9.5 4.5H13" />
      <path d="M9.5 9.5H13" />
    </>
  ),

  // Stacked layers: repositories under a project.
  repositories: (
    <>
      <path d="M8 2 14 5l-6 3-6-3 6-3Z" />
      <path d="m2 8 6 3 6-3" />
      <path d="m2 11 6 3 6-3" />
    </>
  ),

  // A chip: the runtime side.
  agents: (
    <>
      <rect x="4.5" y="4.5" width="7" height="7" rx="1.5" />
      <path d="M6.5 2v2M9.5 2v2M6.5 12v2M9.5 12v2M2 6.5h2M2 9.5h2M12 6.5h2M12 9.5h2" />
    </>
  ),

  // The product mark: a hub with three spokes.
  hub: (
    <>
      <circle cx="8" cy="8" r="2.2" />
      <path d="M8 5.8V2M9.9 9.2l3.2 1.9M6.1 9.2l-3.2 1.9" />
    </>
  ),

  chevron: <path d="m6 3 5 5-5 5" />,

  external: (
    <>
      <path d="M9 3h4v4" />
      <path d="M13 3 7.5 8.5" />
      <path d="M12 9.5V13H3V4h3.5" />
    </>
  ),
};

export function Icon({
  name,
  label,
  size = 16,
}: {
  name: IconName;
  /** Given only when the icon stands alone; otherwise the neighbouring text names it. */
  label?: string;
  size?: number;
}): ReactNode {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      role={label ? 'img' : 'presentation'}
      aria-label={label}
      aria-hidden={label ? undefined : true}
      focusable="false"
      style={{ flex: 'none' }}
    >
      {paths[name]}
    </svg>
  );
}
