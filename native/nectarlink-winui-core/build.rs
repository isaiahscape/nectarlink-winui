// SPDX-License-Identifier: GPL-3.0-or-later
//! Links Windows system libraries and delay-loads `cldapi.dll` so trimmed
//! Windows editions without Cloud Files still load cleanly.

fn main() {
    println!("cargo::rustc-check-cfg=cfg(linux_ffmpeg_video)");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        println!("cargo:rustc-link-lib=dwmapi");
        println!("cargo:rustc-link-lib=ole32");
        println!("cargo:rustc-link-lib=shell32");
        println!("cargo:rustc-link-lib=user32");
        if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
            println!("cargo:rustc-link-arg=/DELAYLOAD:cldapi.dll");
            println!("cargo:rustc-link-lib=delayimp");
        }
    }
}
