/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        cyber: {
          bg: '#08080d',
          panel: '#101019',
          panel2: '#161625',
          border: '#262638',
          text: '#cfd3e6',
          muted: '#7d8298',
          accent: '#00ffc8',
          accent2: '#ff2fb3',
          warn: '#ffb020',
          danger: '#ff4d5e',
        },
      },
      fontFamily: {
        mono: ['"JetBrains Mono"', '"Fira Code"', 'ui-monospace', 'monospace'],
      },
      boxShadow: {
        glow: '0 0 16px rgba(0, 255, 200, 0.25)',
        glowPink: '0 0 16px rgba(255, 47, 179, 0.25)',
      },
    },
  },
  plugins: [],
};
