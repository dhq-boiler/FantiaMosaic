#!/usr/bin/env python
"""
FantiaMosaic 用 YOLO11-OBB 学習スクリプト。

前提:
    pip install ultralytics torch torchvision   (CUDA 対応の torch を推奨)
入力:
    FantiaMosaic アプリの「DLデータセットをエクスポート」で出力された
    フォルダ (images/, labels/, data.yaml を含む)
処理:
    1. train/val を 90/10 で分割し、images_train/labels_train, images_val/labels_val を作成
    2. data.yaml を新形式に書き換え
    3. Ultralytics YOLO11-OBB で学習
    4. 学習済み重みを ONNX (静的入力サイズ) でエクスポート
出力:
    runs/obb/train*/weights/best.pt
    runs/obb/train*/weights/best.onnx   ← C# (ONNX Runtime) で推論する用
"""

from __future__ import annotations

import argparse
import random
import shutil
from pathlib import Path

import yaml


def split_dataset(dataset_dir: Path, val_ratio: float = 0.1, seed: int = 42) -> Path:
    images_dir = dataset_dir / "images"
    labels_dir = dataset_dir / "labels"
    if not images_dir.is_dir() or not labels_dir.is_dir():
        raise SystemExit(f"images/ または labels/ が見つかりません: {dataset_dir}")

    image_paths = sorted(p for p in images_dir.iterdir() if p.suffix.lower() in {".png", ".jpg", ".jpeg", ".webp"})
    if len(image_paths) < 4:
        raise SystemExit("学習に画像が少なすぎます (最低 4 枚必要)")

    rng = random.Random(seed)
    rng.shuffle(image_paths)
    n_val = max(1, int(len(image_paths) * val_ratio))
    val_set = set(image_paths[:n_val])

    split_dir = dataset_dir / "_split"
    if split_dir.exists():
        shutil.rmtree(split_dir)
    (split_dir / "images" / "train").mkdir(parents=True)
    (split_dir / "images" / "val").mkdir(parents=True)
    (split_dir / "labels" / "train").mkdir(parents=True)
    (split_dir / "labels" / "val").mkdir(parents=True)

    for img in image_paths:
        label = labels_dir / (img.stem + ".txt")
        sub = "val" if img in val_set else "train"
        shutil.copy2(img, split_dir / "images" / sub / img.name)
        if label.exists():
            shutil.copy2(label, split_dir / "labels" / sub / label.name)

    data_yaml = {
        "path": str(split_dir.resolve()).replace("\\", "/"),
        "train": "images/train",
        "val": "images/val",
        "names": {0: "occlusion"},
    }
    out_yaml = split_dir / "data.yaml"
    with out_yaml.open("w", encoding="utf-8") as f:
        yaml.safe_dump(data_yaml, f, sort_keys=False, allow_unicode=True)
    print(f"[split] train={len(image_paths) - n_val}  val={n_val}  → {out_yaml}")
    return out_yaml


def train(data_yaml: Path, model: str, epochs: int, imgsz: int, batch: int, device: str) -> Path:
    from ultralytics import YOLO

    yolo = YOLO(model)
    results = yolo.train(
        data=str(data_yaml),
        epochs=epochs,
        imgsz=imgsz,
        batch=batch,
        device=device,
        task="obb",
        project="runs/obb",
        name="train",
        exist_ok=False,
    )
    best = Path(results.save_dir) / "weights" / "best.pt"
    print(f"[train] best weights = {best}")
    return best


def export_onnx(best_pt: Path, imgsz: int) -> Path:
    from ultralytics import YOLO

    yolo = YOLO(str(best_pt))
    onnx_path = yolo.export(format="onnx", imgsz=imgsz, opset=12, simplify=True, dynamic=False)
    onnx_path = Path(onnx_path)
    print(f"[export] ONNX = {onnx_path}")
    return onnx_path


def main():
    ap = argparse.ArgumentParser(description="Train YOLO11-OBB for FantiaMosaic")
    ap.add_argument("dataset", type=Path, help="FantiaMosaic でエクスポートしたデータセットフォルダ")
    ap.add_argument("--model", default="yolo11n-obb.pt", help="ベースモデル (yolo11n-obb.pt が最も軽量)")
    ap.add_argument("--epochs", type=int, default=100)
    ap.add_argument("--imgsz", type=int, default=640)
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--device", default="0", help="'0' = CUDA:0, 'cpu' = CPU")
    ap.add_argument("--val-ratio", type=float, default=0.1)
    args = ap.parse_args()

    dataset = args.dataset.resolve()
    print(f"[input] dataset = {dataset}")
    data_yaml = split_dataset(dataset, args.val_ratio)
    best = train(data_yaml, args.model, args.epochs, args.imgsz, args.batch, args.device)
    export_onnx(best, args.imgsz)
    print("[done] 学習完了。生成された best.onnx を FantiaMosaic アプリで読み込んでくださいっす。")


if __name__ == "__main__":
    main()
