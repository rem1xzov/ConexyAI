import type { SVGProps } from 'react';

/**
 * MOBILE_ICONS: аккуратные линейные иконки для нижней навигации и композера — тонкий штрих,
 * круглые концы, без «мультяшных» заливок. Сделаны по мотивам lucide, чтобы мобильный интерфейс
 * выглядел спокойнее и ближе к Claude.
 */
type IconProps = { size?: number; className?: string } & SVGProps<SVGSVGElement>;

function Svg({ size = 20, className, children, ...rest }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden="true"
      {...rest}
    >
      {children}
    </svg>
  );
}

/** Чат: скруглённое облако с хвостиком. */
export function ChatGlyphIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M20.5 11.6a8.1 8.1 0 0 1-8.2 8.1 8.4 8.4 0 0 1-3.1-.6L4.5 20.5l1.5-4.3a8 8 0 0 1-1.3-4.5 8.1 8.1 0 0 1 8.2-8 8 8 0 0 1 7.6 8Z" />
      <path d="M8.7 11.7h.01M12.3 11.7h.01M15.9 11.7h.01" />
    </Svg>
  );
}

/** Артефакт/искра: тонкая четырёхлучевая звезда для библиотеки промптов. */
export function SparkleGlyphIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M12 3.5c.7 3.9 1.6 5.7 5.4 6.8-3.8 1.1-4.7 2.9-5.4 6.8-.7-3.9-1.6-5.7-5.4-6.8 3.8-1.1 4.7-2.9 5.4-6.8Z" />
      <path d="M18.6 15.4c.3 1.5.6 2.2 2.1 2.6-1.5.4-1.8 1.1-2.1 2.6-.3-1.5-.6-2.2-2.1-2.6 1.5-.4 1.8-1.1 2.1-2.6Z" />
    </Svg>
  );
}

/** Настройки: три полосы с круглыми бегунками. */
export function SlidersGlyphIcon(props: IconProps) {
  return (
    <Svg {...props}>
      <path d="M4 7h9M17 7h3M4 12h3M11 12h9M4 17h11M19 17h1" />
      <circle cx="15" cy="7" r="2" />
      <circle cx="9" cy="12" r="2" />
      <circle cx="17" cy="17" r="2" />
    </Svg>
  );
}
