//! Epic Games Store download adapter for the shared fetch core (`crate::fetch_core`).
//!
//! Production installs use [`stream`] (mode `adaptive`): the manager hands over the manifest
//! bytes and the pending files, fetch_core downloads the chunks in first-use order, and each chunk
//! is written straight into the staged files that use it (shared ones kept in a budgeted cache).
//! [`adaptive`] (mode `lookahead`) is the fallback when the stream engine's space plan does not
//! fit: the Java manifest/delta pass supplies a versioned snapshot, the shared pure storage
//! schedule selects an ordered lookahead, and fetch_core runs concurrently with verified staged
//! assembly. Both keep old outputs until their atomic swap; a synchronous JNI callback marks the
//! sidecar updating before the first replacement. Legacy batch APIs remain available. A missing
//! library uses the bounded Java installer; native plan/runtime failures never silently switch
//! engines.
//!
//! Submodules:
//! - [`adaptive`] — budget planner and bounded overlapping fetch/assembly coordinator.
//! - [`manifest`] — ChunksV4 binary manifest + legacy JSON manifest parsers (1:1 with the Java
//!   `parseManifest` / `parseJsonManifest`).
//! - [`plan`] — install-tag selection, unique-chunk plan, chunk paths / CDN URLs.
//! - [`chunk`] — chunk header parse, zlib inflate, SHA-1 verify, cache write.
//! - [`driver`] — plan → `FetchItem`s → `fetch_core::run_fetch` with the chunk-cache sink.
//! - [`assemble`] — the files from the cache, one sequential write each, SHA-1 checked as they
//!   are written; with a scratch cache (one the caller placed apart from the install) each chunk
//!   is deleted as soon as the last file that uses it is done.
//! - [`stream`] — the adaptive install the manager uses by default: the chunks fetched in
//!   parallel through the fetch core and written straight into staging files as they arrive,
//!   shared chunks kept in a budgeted cache, each file verified and swapped in safely.
//! - [`jni`] — `Java_com_droiddeck_launcher_stores_epic_EpicNative_native*` exports.
//!
//! The selective-install-tag, delta/resume and chunk verification rules mirrored here were ported
//! into the Java manager from GameNative (`service/epic/EpicDownloadManager.kt`,
//! `manifest/ManifestUtils.kt`), itself derived from Legendary; both are GPL-3.0 and this crate
//! carries the same licence (see `Cargo.toml`).

pub mod assemble;
pub mod adaptive;
pub mod chunk;
pub mod driver;
pub mod jni;
pub mod manifest;
pub mod plan;
pub mod stream;

/// User-Agent the Java manager sends on every chunk request (`EpicDownloadManager.UA`).
pub const USER_AGENT: &str =
    "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit";

/// Name of the chunk cache directory under the install dir (`new File(installDir, ".chunks")`).
pub const CHUNK_CACHE_DIR: &str = ".chunks";

/// Fixed pool width of the Java chunk downloader (`Executors.newFixedThreadPool(8)`).
pub const JAVA_POOL_THREADS: usize = 8;

/// logcat tag for every engine line (the Kotlin manager mirrors it into its own debug log).
pub const LOG_TAG: &str = "EpicNative";
