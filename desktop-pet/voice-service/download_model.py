"""One-time public model download; no song, text, or credentials are uploaded."""
import argparse
import hashlib
import json
import os
from pathlib import Path

SERVICE = Path(__file__).resolve().parent
MODELS = {
    "base": ("Qwen/Qwen3-TTS-12Hz-1.7B-Base", "base"),
    "voice-design": ("Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign", "voice_design"),
    "custom-voice": ("Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice", "custom_voice"),
}


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def download_one(root, model_key):
    from huggingface_hub import HfApi, snapshot_download
    repository, expected_type = MODELS[model_key]
    info = HfApi(token=False).model_info(repository, files_metadata=True)
    revision = info.sha
    if not revision:
        raise RuntimeError("The official repository did not return a pinned revision.")
    target = root / repository.rsplit("/", 1)[1]
    print("Downloading official model:", repository, "revision:", revision, flush=True)
    snapshot_download(repo_id=repository, revision=revision, local_dir=target,
                      token=False, max_workers=3)
    # Verify repository-advertised file sizes and LFS SHA-256 values before
    # writing a success receipt. This checks bytes only, without importing a
    # model framework or reserving any GPU memory.
    files = []
    for sibling in info.siblings:
        relative = Path(sibling.rfilename)
        path = (target / relative).resolve()
        if not path.is_relative_to(target.resolve()) or not path.is_file():
            raise RuntimeError("Missing or invalid model file: " + sibling.rfilename)
        size = path.stat().st_size
        if sibling.size is not None and size != sibling.size:
            raise RuntimeError("Model file size mismatch: " + sibling.rfilename)
        entry = {"path": sibling.rfilename, "bytes": size}
        lfs = sibling.lfs
        expected_sha = getattr(lfs, "sha256", None) if lfs else None
        if expected_sha:
            print("Verifying:", sibling.rfilename, flush=True)
            actual_sha = sha256(path)
            if actual_sha.lower() != expected_sha.lower():
                raise RuntimeError("Model SHA-256 mismatch: " + sibling.rfilename)
            entry["sha256"] = actual_sha
        files.append(entry)
    config = json.loads((target / "config.json").read_text(encoding="utf-8"))
    if config.get("tts_model_type") != expected_type:
        raise RuntimeError("Unexpected TTS model type in the downloaded configuration.")
    receipt = {
        "repository": repository, "revision": revision, "model_path": str(target),
        "source": "https://huggingface.co/" + repository, "status": "downloaded",
        "tts_model_type": expected_type, "verified_files": files,
        "verification": "Repository file sizes and advertised LFS SHA-256; no model loaded",
    }
    # Keep the historical Base receipt filename for setup-script compatibility;
    # downloading another model must not replace that existing receipt.
    name = "model-download.json" if model_key == "base" else "model-download-" + model_key + ".json"
    receipt_path = root / name
    temporary = receipt_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(receipt, indent=2), encoding="utf-8")
    temporary.replace(receipt_path)
    # Some Windows launchers expose cp1252 stdout despite a Unicode workspace.
    # The receipt contains the full path; keep console confirmation portable.
    print("Official model downloaded and verified:", repository, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-root", type=Path)
    parser.add_argument("--model", choices=(*MODELS, "all"), default="base",
                        help="Voice model to download; default: base (existing reference voice)")
    args = parser.parse_args()
    root = args.model_root.resolve() if args.model_root else SERVICE.parent.parent / ".tools" / "deepseek-voice-models"
    root.mkdir(parents=True, exist_ok=True)
    os.environ["HF_HOME"] = str(root / "hf-cache")
    os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["DO_NOT_TRACK"] = "1"
    for key in MODELS if args.model == "all" else [args.model]:
        download_one(root, key)


if __name__ == "__main__":
    main()
