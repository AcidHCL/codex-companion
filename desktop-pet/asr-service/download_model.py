"""Installation-only download from the original faster-whisper maintainer."""
import argparse
import json
import os
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--model-root", type=Path)
args = parser.parse_args()
service = Path(__file__).resolve().parent
root = args.model_root.resolve() if args.model_root else service.parent.parent / ".tools" / "deepseek-asr-models"
root.mkdir(parents=True, exist_ok=True)
os.environ["HF_HOME"] = str(root / "hf-cache")
os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["DO_NOT_TRACK"] = "1"


def main():
    from huggingface_hub import HfApi, snapshot_download
    repository = "Systran/faster-whisper-small"
    revision = HfApi(token=False).model_info(repository).sha
    target = root / "faster-whisper-small"
    snapshot_download(repo_id=repository, revision=revision, local_dir=target,
                      token=False, max_workers=3)
    (root / "model-download.json").write_text(json.dumps({
        "repository": repository, "revision": revision, "model_path": str(target),
        "source": "https://huggingface.co/" + repository,
        "status": "downloaded", "runtime": "CPU int8",
    }, indent=2), encoding="utf-8")
    print("ASR model downloaded:", target)


if __name__ == "__main__":
    main()
