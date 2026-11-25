// SPDX-License-Identifier: GPL-3.0-or-later
//! `cargo xtask icons`: renders the NectarLink mark (`mark.rs`) into the
//! multi-resolution `.ico` and Sparse Package PNG assets shipped by the WinUI app.

use std::{fs, path::Path};

use anyhow::{Context, Result};

#[path = "../../../native/nectarlink-winui-core/src/mark.rs"]
#[allow(dead_code)]
mod mark;

/// Icon sizes in the .exe: what Explorer, the taskbar, and Start ask for at 100-200% scale.
const ICON_SIZES: &[u32] = &[16, 20, 24, 32, 40, 48, 64, 256];

pub fn run() -> Result<()> {
    let root = crate::repo_root();
    let assets_dir = root.join("winui/NectarLink.WinUI/Assets");
    fs::create_dir_all(&assets_dir)?;

    write_if_changed(&assets_dir.join("nectarlink.ico"), &render_ico())?;
    write_if_changed(&assets_dir.join("Square44x44Logo.png"), &render_png(44))?;
    write_if_changed(&assets_dir.join("Square150x150Logo.png"), &render_png(150))?;
    Ok(())
}

fn write_if_changed(path: &Path, bytes: &[u8]) -> Result<()> {
    if fs::read(path).ok().as_deref() != Some(bytes) {
        fs::write(path, bytes).with_context(|| format!("writing {}", path.display()))?;
        println!("wrote {}", path.display());
    } else {
        println!("up to date {}", path.display());
    }
    Ok(())
}

/// Builds a Windows `.ico` file containing 32-bit bottom-up BGRA bitmaps with alpha.
fn render_ico() -> Vec<u8> {
    let images: Vec<Vec<u8>> = ICON_SIZES
        .iter()
        .map(|&size| {
            let rgba = mark::render(size as usize);
            let mask_row = size.div_ceil(32) * 4;
            let mut image = Vec::new();
            for field in [40u32, size, size * 2] {
                image.extend_from_slice(&field.to_le_bytes());
            }
            image.extend_from_slice(&1u16.to_le_bytes());
            image.extend_from_slice(&32u16.to_le_bytes());
            image.extend_from_slice(&[0; 24]);
            for row in rgba.chunks_exact(size as usize * 4).rev() {
                for p in row.chunks_exact(4) {
                    image.extend_from_slice(&[p[2], p[1], p[0], p[3]]);
                }
            }
            image.resize(image.len() + (mask_row * size) as usize, 0);
            image
        })
        .collect();

    let count = u16::try_from(images.len()).expect("icon count fits u16");
    let mut ico = Vec::new();
    for field in [0u16, 1, count] {
        ico.extend_from_slice(&field.to_le_bytes());
    }
    let mut offset = 6 + 16 * images.len();
    for (&size, image) in ICON_SIZES.iter().zip(&images) {
        let side = u8::try_from(size).unwrap_or(0);
        ico.extend_from_slice(&[side, side, 0, 0]);
        ico.extend_from_slice(&1u16.to_le_bytes());
        ico.extend_from_slice(&32u16.to_le_bytes());
        ico.extend_from_slice(&u32::try_from(image.len()).expect("image fits u32").to_le_bytes());
        ico.extend_from_slice(&u32::try_from(offset).expect("offset fits u32").to_le_bytes());
        offset += image.len();
    }
    for image in images {
        ico.extend_from_slice(&image);
    }
    ico
}

/// Encodes straight-alpha RGBA pixels as a valid uncompressed 32-bit RGBA PNG.
fn render_png(size: u32) -> Vec<u8> {
    let rgba = mark::render(size as usize);
    let stride = size as usize * 4;
    let mut raw = Vec::with_capacity((stride + 1) * size as usize);
    for row in rgba.chunks_exact(stride) {
        raw.push(0); // filter type 0 (None)
        raw.extend_from_slice(row);
    }

    // Zlib stream with stored (uncompressed) deflate blocks (max 65535 bytes each).
    let mut zlib = Vec::with_capacity(raw.len() + 32);
    zlib.extend_from_slice(&[0x78, 0x01]);
    let chunks: Vec<&[u8]> = raw.chunks(65535).collect();
    for (idx, chunk) in chunks.iter().enumerate() {
        let is_last = u8::from(idx + 1 == chunks.len());
        let len = chunk.len() as u16;
        zlib.push(is_last);
        zlib.extend_from_slice(&len.to_le_bytes());
        zlib.extend_from_slice(&(!len).to_le_bytes());
        zlib.extend_from_slice(chunk);
    }
    zlib.extend_from_slice(&adler32(&raw).to_be_bytes());

    let mut png = Vec::with_capacity(64 + zlib.len());
    png.extend_from_slice(b"\x89PNG\r\n\x1a\n");

    let mut ihdr = Vec::with_capacity(13);
    ihdr.extend_from_slice(&size.to_be_bytes());
    ihdr.extend_from_slice(&size.to_be_bytes());
    ihdr.extend_from_slice(&[8, 6, 0, 0, 0]); // 8-bit RGBA
    push_chunk(&mut png, b"IHDR", &ihdr);
    push_chunk(&mut png, b"IDAT", &zlib);
    push_chunk(&mut png, b"IEND", &[]);
    png
}

fn push_chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(data);
    let mut crc_input = Vec::with_capacity(4 + data.len());
    crc_input.extend_from_slice(kind);
    crc_input.extend_from_slice(data);
    out.extend_from_slice(&crc32(&crc_input).to_be_bytes());
}

fn adler32(data: &[u8]) -> u32 {
    const MOD: u32 = 65521;
    let (mut s1, mut s2) = (1u32, 0u32);
    for &b in data {
        s1 = (s1 + u32::from(b)) % MOD;
        s2 = (s2 + s1) % MOD;
    }
    (s2 << 16) | s1
}

fn crc32(data: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for &b in data {
        crc ^= u32::from(b);
        for _ in 0..8 {
            let mask = (crc & 1).wrapping_neg();
            crc = (crc >> 1) ^ (0xEDB8_8320 & mask);
        }
    }
    !crc
}
