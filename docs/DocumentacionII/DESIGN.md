# Design System Document: Professional Trading Analytics

## 1. Overview & Creative North Star
**Creative North Star: "The Obsidian Lens"**

This design system is engineered for the high-stakes environment of professional trading. It moves away from the "cluttered terminal" aesthetic of legacy platforms and toward an experience of **Atmospheric Precision**. The system focuses on reducing cognitive load by treating the interface as a dark, multi-layered obsidian landscape where data is the only light source.

To break the "template" look, we utilize **Intentional Asymmetry**. Dashboards should not be perfectly balanced grids; instead, use the Spacing Scale to create "active voids"—pockets of breathing room that guide the eye toward critical movement in market data. We prioritize tonal depth over structural lines, ensuring the platform feels like a sophisticated tool rather than a generic website.

---

## 2. Colors: Tonal Architecture
The palette is built on a foundation of deep anthracites. This is not a "true black" system; it is a system of shadows and light-emission.

### Palette Highlights
- **Background (`#131313`):** Our foundation. A matte, deep anthracite that absorbs light.
- **Primary / Acent (`#d0f2ff` / `#4cd6fb`):** An electric, soft cian. This color is used sparingly for execution actions and trend highlights.
- **Surface Tiers:** We use `surface_container` tokens to create hierarchy.

### The "No-Line" Rule
**Explicit Instruction:** Designers are prohibited from using 1px solid borders to section off high-level modules. Boundary definition must be achieved through:
1.  **Background Shifts:** Placing a `surface_container_low` (`#1c1b1b`) card on top of the `background` (`#131313`).
2.  **Negative Space:** Using the `spacing-10` or `spacing-12` tokens to create separation through distance.

### The "Glass & Gradient" Rule
Floating elements (modals, dropdowns, hovered tooltips) must use **Glassmorphism**. 
- **Recipe:** `surface_variant` (`#353534`) at 60% opacity with a `20px` backdrop-blur. 
- Main CTAs should use a subtle linear gradient from `primary` (`#d0f2ff`) to `primary_container` (`#72deff`) at a 135-degree angle to provide a premium, tactile feel.

---

## 3. Typography: Editorial Authority
We pair **Manrope** for high-level data display and **Inter** for functional reading. This combination ensures that numbers feel like "headlines" while labels remain ultra-legible.

- **Display-LG (Manrope, 3.5rem):** Reserved for Portfolio Totals or primary P&L. It conveys dominance and clarity.
- **Headline-SM (Manrope, 1.5rem):** Used for widget titles.
- **Body-MD (Inter, 0.875rem):** The workhorse for data tables and descriptions.
- **Label-SM (Inter, 0.6875rem):** Used for micro-data, timestamps, and metadata. 

**Hierarchical Strategy:** Use `on_surface_variant` (`#bac9cc`) for labels to push them into the background, and `on_surface` (`#e5e2e1`) for the data values themselves. This creates a clear "Label: **Value**" visual priority.

---

## 4. Elevation & Depth: Tonal Layering
We reject traditional drop shadows in favor of **Tonal Stacking**.

### The Layering Principle
Hierarchy is defined by "Value Lift." The closer an object is to the user, the lighter its background:
1.  **Level 0 (Base):** `surface` (`#131313`) - The main application canvas.
2.  **Level 1 (Cards):** `surface_container_low` (`#1c1b1b`) - Standard informational modules.
3.  **Level 2 (Active/Hover):** `surface_container_high` (`#2a2a2a`) - Interactive elements or focused cards.

### Ambient Shadows
For floating elements like "Trade Execution" panels:
- **Shadow:** 0px 12px 32px rgba(0, 0, 0, 0.4).
- **The "Ghost Border" Fallback:** If a border is required for accessibility, use `outline_variant` (`#3b494c`) at **15% opacity**. It should be felt, not seen.

---

## 5. Components: Functional Elegance

### Buttons
- **Primary:** Gradient fill (`primary` to `primary_container`), `on_primary_fixed` text. Roundedness: `md` (`0.375rem`).
- **Secondary:** `surface_container_highest` fill, `on_surface` text. No border.
- **Tertiary:** Ghost style. No fill. `primary` text.

### Input Fields
- **Default State:** `surface_container_highest` background. No border.
- **Focus State:** `ghost border` using `primary` at 30% opacity. 
- **Validation:** Use `error` (`#ffb4ab`) for text, never for the entire background.

### Cards & Lists
- **Prohibition:** Do not use horizontal dividers between list items. 
- **Execution:** Use a background shift on hover (`surface_container_high`) and `spacing-3` of vertical padding to separate data rows. This maintains the "Obsidian" flow without cutting the UI into fragments.

### Specialized Components: The Data Strip
For market tickers, use a `surface_container_lowest` (`#0e0e0e`) bar with a 1px `outline_variant` at 10% opacity on the top and bottom edge only. This creates a "track" for information to flow through.

---

## 6. Do's and Don'ts

### Do
- **Do** use `primary_fixed_dim` for icons to give them a soft, "glowing" neon effect against the dark background.
- **Do** use `roundedness-lg` (`0.5rem`) for main dashboard cards to soften the professional aesthetic.
- **Do** maximize the use of `spacing-8` and `spacing-10` to prevent the platform from feeling like a crowded spreadsheet.

### Don't
- **Don't** use pure white (`#FFFFFF`) for text. It causes eye strain in dark environments. Always use `on_surface` (`#e5e2e1`).
- **Don't** use high-contrast borders. A 100% opaque grey border is the quickest way to make a premium design look "cheap."
- **Don't** use standard "Success Green." Rely on the `primary` (cian) for positive actions and `error` for negative ones to maintain the signature color identity.