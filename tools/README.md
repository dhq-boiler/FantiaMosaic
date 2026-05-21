# FantiaMosaic - DL 学習ツール

YOLO11-OBB で「隠蔽すべき領域」を回転矩形検出するモデルを学習するためのツール群っす。

## 必要環境

- Python 3.10+
- NVIDIA GPU + CUDA 12.x (CPU 学習も可能だが激遅)
- `pip install ultralytics pyyaml`

## 使い方

### 1. データ収集

FantiaMosaic アプリで：
1. 学習に使いたい画像を一括ロード
2. 各画像で**回転矩形を描く**（ハンドル編集で位置・サイズ・角度を調整）
3. メニュー `ファイル → DLデータセットをエクスポート` で `<出力先>` フォルダに保存

最低 50 枚、できれば 100〜200 枚あると安定するっす。

### 2. 学習

```powershell
python tools/train_yolo_obb.py <データセットフォルダ> --epochs 100 --imgsz 640 --batch 8 --device 0
```

主要オプション：
| 引数 | デフォルト | 説明 |
|---|---|---|
| `--model` | `yolo11n-obb.pt` | ベースモデル。`yolo11s-obb.pt`/`yolo11m-obb.pt` で精度↑速度↓ |
| `--epochs` | 100 | 100〜200 推奨。データが少ない時は augmentation 強めに |
| `--imgsz` | 640 | 高解像度画像なら 1024 など |
| `--batch` | 8 | GPU メモリに合わせて |
| `--device` | `0` | `0`=CUDA:0、`cpu`=CPU |
| `--val-ratio` | 0.1 | val 分割比 |

学習中は `runs/obb/train*/` に metrics と画像サンプルが書き出されるっす。

### 3. ONNX エクスポート

`train` 完了時点で `best.onnx` も自動生成されてるっす（同フォルダの `weights/` 下）。

### 4. FantiaMosaic アプリで使う

アプリの `DL 検出` ボタンで `best.onnx` を選択して読み込むっす（後続実装）。

## トラブルシューティング

- **「torch.cuda.is_available() が False」**: CUDA 対応の torch をインストール:
  ```
  pip install torch torchvision --index-url https://download.pytorch.org/whl/cu121
  ```
- **OOM (Out of Memory)**: `--batch` を 4 / 2 と下げる、または `--imgsz` を 512 / 480 に
- **過学習**: `--epochs` を減らす、データ数増やす、augmentation 強める
