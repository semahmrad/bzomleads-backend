Restaurant Signature Mapping

Base source:
- Inspired by `C:\Users\SEMAH\Desktop\pgray\exploitGemini-courses-web\resturant maquette`

Dynamic zones replaced per restaurant:
- `brand`: name, category, generated logo or uploaded logo
- `welcome splash`: hero image, eyebrow, title, subtitle
- `hero`: headline, subtitle, description, WhatsApp CTA, services CTA, Google Maps CTA
- `hero stats`: rating, review count, phone, hours, address
- `about`: story text, location, rating block, contact block, layered photos
- `services`: inferred or extracted services with image-backed cards
- `gallery`: restaurant photos and captions
- `reviews`: rating stars, review count, public highlights, Google review links
- `contact`: address, phone, email, WhatsApp, Google Maps embed
- `faq`: AI-generated answers based on category, address, hours, and services
- `seo`: title, description, keywords, structured data, social preview image

Image priority:
1. User uploaded images
2. Valid extracted business photos
3. Official website and social discovery
4. Public web image search
5. Curated restaurant fallback photos from the maquette visual direction
6. Local SVG placeholders only as last resort

Visual variations kept on the same base maquette:
- random theme colors
- typography pairing
- motion style
- section order
- gallery image mix
- small restaurant variant flag (`classic` / `editorial`)

Images explicitly rejected from the dynamic pipeline:
- static maps
- street view tiles
- map tiles
- logos/icons/favicons
- sprites/placeholders/decorative assets
