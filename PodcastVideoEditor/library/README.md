# Built-in Library Assets

Place image assets in the appropriate category subfolder.
The application scans this folder on startup and registers
any new images into the global library database.

## Supported formats
- PNG, JPG, JPEG, WebP, BMP, GIF

## Folder structure
```
library/
├── logos/       — Logo images, branding marks
├── icons/       — UI icons, social media icons
├── overlays/    — Lower thirds, vignettes, frames
└── borders/     — Decorative borders, photo frames
```

Assets placed here are marked as "built-in" and cannot be
deleted by the user from the library UI.
