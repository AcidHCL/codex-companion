"""One-time public model download; no song, text, or credentials are uploaded."""
import argparse
import json
import os
from pathlib import Path

SERVICE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--model-root", type=Path)
args = parser.parse_args()
ROOT = args.model_root.resolve() if args.model_root else SERVICE.parent.parent / ".tools" / "deepseek-voice-models"
ROOT.mkdir(parents=True, exist_ok=True)
os.environ["HF_HOME"] = str(ROOT / "hf-cache")
os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["DO_NOT_TRACK"] = "1"


def main():
    from huggingface_hub import HfApi, snapshot_download
    repository = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
    revision = HfApi(token=False).model_info(repository).sha
    target = ROOT / repository.rsplit("/", 1)[1]
    snapshot_download(repo_id=repository, revision=revision, local_dir=target,
                      token=False, max_workers=3)
    (ROOT / "model-download.json").write_text(json.dumps({
        "repository": repository, "revision": revision, "model_path": str(target),
        "source": "https://huggingface.co/" + repository, "status": "downloaded",
    }, indent=2), encoding="utf-8")
    print("Official model downloaded:", target)


if __name__ == "__main__":
    main()
