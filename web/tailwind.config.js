/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        ink: { 950: '#09090b', 900: '#0f0f12', 850: '#141417', 800: '#18181b', 700: '#232329', 600: '#303038' },
        accent: { DEFAULT: '#a78bfa', soft: '#7c3aed', warm: '#34d399' },
      },
      fontFamily: { sans: ['Inter', 'ui-sans-serif', 'system-ui', 'Segoe UI', 'sans-serif'] },
    },
  },
  plugins: [],
};
