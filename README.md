# vd-switch

Windows の仮想デスクトップをキーシミュレーションを介さず直接切り替えるツール。
タスクトレイ常駐型で、ネットワーク経由のフットスイッチにも対応します。

## 背景・動機

Windows の仮想デスクトップは `Ctrl + Win + 左右キー` で切り替えられますが、**mstsc.exe（リモートデスクトップ接続）** などのアプリにフォーカスがあるとショートカットが奪われて機能しません。Logi Options+ でマウスのサイドボタンに割り当てても、キー入力シミュレーション経由である限り同じ問題が生じます。

根本的な解決には、キーシミュレーションを介さず **仮想デスクトップ API を直接呼び出す** 必要があります。Windows は公式のパブリック API を提供していないため、Shell 内部の COM インターフェースを利用します。

## 仕組み

[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) の COM インターフェース定義（Windows 11 24H2 以降対応版）をソースとして取り込み、外部 DLL への依存なしに仮想デスクトップを操作します。

- 外部 DLL（VirtualDesktopAccessor.dll 等）不要
- `vd-switch.exe` 単体で動作
- .NET 9 self-contained ビルド（ランタイムのインストール不要）

## 動作要件

- Windows 11 24H2 以降

## 使い方

### トレイ常駐モード（引数なし）

```
vd-switch.exe
```

タスクトレイにアイコンが表示されます。

- **右クリックメニュー**: Desktop 1 / Desktop 2 / Desktop 3 で即時切り替え、Quit で終了
- **フットスイッチ連携**: `vd-switch-sender`（後述）から IPv6 マルチキャストでパケットを受信し、自動でデスクトップを切り替えます

| パケット | 動作 |
|---|---|
| `KEY_A DOWN` | 前のデスクトップへ |
| `KEY_B DOWN` | Task View を開く |
| `KEY_C DOWN` | 次のデスクトップへ |
| `KEY_X UP` 等 | 受信するが無視（UDP のためパケット欠落あり得る） |

### CLI モード（引数あり）

```
vd-switch <番号>      # 指定した番号のデスクトップに切り替え（0始まり）
vd-switch --current   # 現在のデスクトップ番号を表示
vd-switch --count     # デスクトップの総数を表示
```

## フットスイッチ連携（vd-switch-sender）

Linux マシン（Raspberry Pi 等）に USB フットスイッチを接続し、`sender/vd-switch-sender.py` を使って IPv6 リンクローカルマルチキャストでコマンドを送信できます。

```
# sender/ディレクトリで
make install
systemctl enable --now vd-switch-sender
```

フットスイッチのキー a/b/c をそれぞれ Desktop 1/2/3 に対応させます。キーの意味は受信側（vd-switch.exe）が決定します。

### マルチキャスト仕様

| 項目 | 値 |
|---|---|
| アドレス | `ff12::7664:7377` |
| ポート | `5356` |
| プロトコル | IPv6 リンクローカルマルチキャスト UDP |
| ペイロード | `KEY_X DOWN` / `KEY_X UP`（UTF-8 テキスト、UP は欠落あり得る） |

## ビルド方法

.NET 9 SDK が必要です。Linux からのクロスコンパイルも可能です。

```
dotnet publish -c Release
```

成果物は `bin/Release/net9.0-windows/win-x64/publish/vd-switch.exe` に出力されます。

## 注意事項

Windows の仮想デスクトップ内部 COM インターフェースは非公式であり、**Windows Update によって動作しなくなる可能性があります**。Windows のメジャーアップデート後に動作しなくなった場合は、[MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop) の最新版を確認してください。

### セキュリティ上の注意

`vd-switch-sender` は対象デバイスのキーストロークを **LAN 内にマルチキャスト送信** します。`--grab` オプションを使わない場合、sender の動作中に同一デバイスで通常のキー入力を行うと、そのキーコードが LAN 上に流出します。sender を動作させるホストでは、対象 HID デバイスを専用のフットスイッチ等に限定するか、`--grab` で占有することを推奨します。

## 拡張アイディア

現在の実装はフットスイッチ（EV_KEY デバイス）の KEY_A/B/C を対象としていますが、以下の方向への拡張が考えられます。

### デバイス識別子のペイロードへの付加

複数の異なる HID デバイスを使い分ける場合、receiver 側でどのデバイスからの入力かを判別できると便利です。ペイロードに `vendor:product` を付加する案が有効です。

```
KEY_A DOWN 046d:c52b
KEY_A UP   046d:c52b
```

- USB / Bluetooth 接続デバイスは `device.info.vendor` と `device.info.product` から取得できる
- それ以外（仮想デバイス等）は `0000:0000` またはブランクとする
- receiver 側でデバイスごとに異なるアクションにマッピングするといった用途に使える

### キー以外のデバイスへの対応

evdev は EV_KEY（ボタン）以外に EV_REL（ホイール・相対移動）、EV_ABS（ジョイスティック軸・タッチパネル）も扱える。ジョイスティックのボタンは EV_KEY と同じ扱いなので現状のコードでほぼそのまま拾える。ホイールや軸（連続量）への対応は閾値判断が必要になり、プロトコルの拡張も伴うため別途検討が必要。

## ライセンス

COM インターフェース定義部分は [MScholtes/VirtualDesktop](https://github.com/MScholtes/VirtualDesktop)（MIT License）に基づきます。詳細は [LICENSE](LICENSE) を参照してください。
