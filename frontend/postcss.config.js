/**
 * STICKY_HOVER: на тач-экране `:hover` «прилипает» к кнопке после тапа и держится, пока не тапнешь
 * в другое место, — кнопки выглядели нажатыми/залипшими. Все правила с `:hover` (свои и Tailwind
 * `hover:`) оборачиваются в `@media (hover: hover)`: на телефоне hover-стилей нет вовсе, на ПК с
 * мышью всё как раньше.
 */
const hoverOnlyWithPointer = () => ({
  postcssPlugin: 'hover-only-with-pointer',
  Once(root, { atRule }) {
    root.walkRules((rule) => {
      if (!rule.selector.includes(':hover')) return;
      for (let parent = rule.parent; parent; parent = parent.parent) {
        if (parent.type === 'atrule' && parent.name === 'media' && parent.params.includes('hover: hover')) return;
        if (parent.type === 'atrule' && /keyframes$/.test(parent.name)) return;
      }
      const media = atRule({ name: 'media', params: '(hover: hover)' });
      rule.replaceWith(media);
      media.append(rule);
    });
  },
});
hoverOnlyWithPointer.postcss = true;

export default {
  plugins: [
    (await import('tailwindcss')).default,
    hoverOnlyWithPointer,
    (await import('autoprefixer')).default,
  ],
};
