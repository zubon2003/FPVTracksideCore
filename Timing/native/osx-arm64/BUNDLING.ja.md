# macOS arm64 用 OpenCV dylib 同梱手順

このディレクトリには、Mac (Apple Silicon) でビルドした
`libOpenCvSharpExtern.dylib` と、それが依存する OpenCV 4.11 系の
`libopencv_*.dylib` 一式を **`@loader_path` 参照に書き換えた状態で**
コミットします。

これにより、エンドユーザの Mac に `brew install opencv@4` が無くても
ArUco 検出が動くようになります。

---

## 1. 前提

- Apple Silicon Mac (arm64)
- `brew install opencv@4` 済み（4.11 系。`brew info opencv` で確認）
  - 既に `libOpenCvSharpExtern.dylib` のビルドに使ったものと同一バージョン
- Xcode Command Line Tools (`otool`, `install_name_tool` が使える)
- このリポジトリを clone 済み、ブランチは `aruco-debug` ベースで作業

```bash
cd <repo>/Timing/native/osx-arm64
ls -la libOpenCvSharpExtern.dylib  # 既にコミット済みのものが見えるはず
```

---

## 2. 必要な dylib の特定

`libOpenCvSharpExtern.dylib` がリンクしている OpenCV 本体を確認します。

```bash
otool -L libOpenCvSharpExtern.dylib | grep -i opencv
```

出力例（実際のパスは brew の場所による）:

```
/opt/homebrew/opt/opencv/lib/libopencv_aruco.411.dylib   (compatibility version 4.11.0, current version 4.11.0)
/opt/homebrew/opt/opencv/lib/libopencv_calib3d.411.dylib ...
/opt/homebrew/opt/opencv/lib/libopencv_imgproc.411.dylib ...
/opt/homebrew/opt/opencv/lib/libopencv_core.411.dylib    ...
... (実装次第で他にも features2d / objdetect / video / dnn / flann 等)
```

ここに出てきた **すべての** `libopencv_*.dylib` をこのディレクトリに
コピーする必要があります。

---

## 3. コピー & 推移的依存の取り込み

OpenCV 本体は相互に依存しているので、コピーした各 dylib に対しても
再帰的に `otool -L` を走らせて、不足する `libopencv_*.dylib` が
あれば追加します。さらに `libtbb` / `libpng` / `libjpeg` 等の
非システム依存も含める必要があります（`/usr/lib/` や `/System/`
始まりのものは Mac 標準なので除外）。

```bash
# brew opencv の lib ディレクトリを特定
OPENCV_LIB=$(brew --prefix opencv)/lib
echo "OPENCV_LIB=$OPENCV_LIB"

# 第1段階: libOpenCvSharpExtern.dylib の参照を参考に、しかし参照していないものも含めビルドしたOpenCVをコピー(ここは手作業に変更) 全部をコピーするのは最終的に別のdylibから呼び出されて全てが必要になったため
otool -L libOpenCvSharpExtern.dylib

cp ~/src/opencv_build/opencv/build/lib/*411* ./

# 第2段階: OpenCV の dylib を起点にして再起的に必要な dylib を取り込む (固定点に達するまで反復)

```bash
#!/bin/bash

# 収集先はカレントディレクトリ
TARGET_DIR="."

# 処理済みファイルの記録用ファイル（絶対パスを保存）
PROCESSED_FILE=$(mktemp)
trap 'rm -f "$PROCESSED_FILE"' EXIT

# パスを論理的に正規化する関数（シンボリックリンクは維持しつつ .. を解決）
normalize_path() {
    local path="$1"
    local dir
    local base
    if [ -d "$path" ]; then
        dir="$path"
        base=""
    else
        dir=$(dirname "$path")
        base=$(basename "$path")
    fi
    if [ -d "$dir" ]; then
        local resolved_dir
        resolved_dir=$(cd "$dir" && pwd -L)
        if [ -n "$base" ]; then
            echo "${resolved_dir}/${base}"
        else
            echo "${resolved_dir}"
        fi
    else
        echo "$path"
    fi
}

# 探索待ちキュー
queue=()

# カレントディレクトリにある既存の .dylib を初期キューに追加
for f in *.dylib; do
    if [ -e "$f" ]; then
        abs_path=$(normalize_path "$f")
        install_name=$(otool -D "$abs_path" 2>/dev/null | tail -n 1)
        if [[ "$install_name" == /opt/homebrew/* ]]; then
            queue+=($(normalize_path "$install_name"))
        else
            queue+=("$abs_path")
        fi
    fi
done

while [ ${#queue[@]} -gt 0 ]; do
    # キューから先頭を取り出す
    current="${queue[0]}"
    queue=("${queue[@]:1}")

    # すでに処理済み（同じ絶対パス）かチェック
    if grep -Fxq "$current" "$PROCESSED_FILE" 2>/dev/null; then
        continue
    fi
    echo "$current" >> "$PROCESSED_FILE"

    echo "Scanning: $current"

    filename=$(basename "$current")
    # コピー先でのファイル存在チェックとコピー
    if [ ! -f "$TARGET_DIR/$filename" ]; then
        if [ -f "$current" ]; then
            if cp "$current" "$TARGET_DIR/"; then
                echo "Copied: $current -> $TARGET_DIR/$filename"
            else
                echo "Failed to copy: $current"
                continue
            fi
        else
            echo "Warning: Source file not found: $current"
            # 実体ファイルが見つからない場合は、カレントディレクトリに同名ファイルがあればそれをスキャンする
            if [ -f "$TARGET_DIR/$filename" ]; then
                current=$(normalize_path "$TARGET_DIR/$filename")
            else
                continue
            fi
        fi
    fi

    # otool -L を使用して依存関係のパスを抽出
    deps=$(otool -L "$current" 2>/dev/null | sed -E 's/^[[:space:]]*//; s/ \(.*\)$//' | grep -E '^(/opt/homebrew/|@rpath/|@loader_path/)')

    for dep in $deps; do
        # パターン1: /opt/homebrew/ 内の絶対パス
        if [[ "$dep" == /opt/homebrew/*.dylib ]]; then
            normalized_dep=$(normalize_path "$dep")
            if ! grep -Fxq "$normalized_dep" "$PROCESSED_FILE" 2>/dev/null; then
                queue+=("$normalized_dep")
            fi

        # パターン2: @rpath または @loader_path を含む相対パス
        elif [[ "$dep" == @*/*.dylib ]]; then
            filename=$(basename "$dep")
            
            # 相対パスを解決するためのベースとなるディレクトリを取得
            base_dir=$(dirname "$current")
            
            if [[ "$dep" == @rpath/* ]]; then
                # @rpath の場合は LC_RPATH セクションからパスリストを取得
                rpaths=$(otool -l "$current" 2>/dev/null | grep -A 2 "LC_RPATH" | grep "path" | awk '{print $2}')
                
                for rpath in $rpaths; do
                    [[ -z "$rpath" ]] && continue
                    # @loader_path を base_dir に置換して絶対パスに変換
                    resolved_rpath=$(echo "$rpath" | sed "s|@loader_path|$base_dir|g")
                    
                    # 末尾のスラッシュを整理して結合
                    resolved_rpath_clean="${resolved_rpath%/}"
                    resolved_dep="${resolved_rpath_clean}/${filename}"
                    
                    if [ -f "$resolved_dep" ]; then
                        if [[ "$resolved_dep" == /opt/homebrew/* ]]; then
                            normalized_dep=$(normalize_path "$resolved_dep")
                            if ! grep -Fxq "$normalized_dep" "$PROCESSED_FILE" 2>/dev/null; then
                                queue+=("$normalized_dep")
                            fi
                        fi
                    fi
                done
            else
                # @loader_path の場合
                resolved_dep=$(echo "$dep" | sed "s|@loader_path|$base_dir|g")

                if [ -f "$resolved_dep" ]; then
                    if [[ "$resolved_dep" == /opt/homebrew/* ]]; then
                        normalized_dep=$(normalize_path "$resolved_dep")
                        if ! grep -Fxq "$normalized_dep" "$PROCESSED_FILE" 2>/dev/null; then
                            queue+=("$normalized_dep")
                        fi
                    fi
                fi
            fi
        fi
    done
done

echo "Done. All collected dylibs are in the current directory."
```

---

## 4. install_name の書き換え

`@loader_path/` 参照に統一し、エンドユーザ Mac に brew が無くても
同じディレクトリの dylib が使われるようにします。

```bash
#!/bin/bash

# Target: All dylibs in the current directory
TARGET_DIR="."

echo "Starting to update paths and verify LC_RPATH for dylib files in $TARGET_DIR..."
echo "--------------------------------------------------"

for lib in "$TARGET_DIR"/*.dylib; do
    # Skip if no dylib files are found
    [ -f "$lib" ] || continue

    filename=$(basename "$lib")
    
    # 1. Get current ID (install name)
    current_id=$(otool -D "$lib" 2>/dev/null | tail -n 1)
    
    id_to_change=""
    if [[ "$current_id" == /opt/homebrew/* ]]; then
        id_to_change="$current_id"
    fi

    # 2. Get dependencies that need to be changed
    # Extract dependencies containing "/opt/homebrew/", excluding the library's own ID
    deps_to_change=()
    all_deps=$(otool -L "$lib" 2>/dev/null | grep "/opt/homebrew/" | awk '{print $1}')
    for dep in $all_deps; do
        if [ "$dep" != "$current_id" ]; then
            # Avoid duplicates
            if [[ ! " ${deps_to_change[*]} " =~ " ${dep} " ]]; then
                deps_to_change+=("$dep")
            fi
        fi
    done

    # 3. Check if @loader_path is already in LC_RPATH
    rpaths=$(otool -l "$lib" 2>/dev/null | grep -A 2 "LC_RPATH" | grep "path" | awk '{print $2}')
    has_loader_path=false
    for rpath in $rpaths; do
        if [ "$rpath" = "@loader_path" ]; then
            has_loader_path=true
            break
        fi
    done

    # Determine if any changes are needed
    need_change=false
    if [ -n "$id_to_change" ] || [ ${#deps_to_change[@]} -gt 0 ] || [ "$has_loader_path" = false ]; then
        need_change=true
    fi

    if [ "$need_change" = true ]; then
        echo "Updating: $filename"

        # 4. Remove signature before making changes
        echo "  -> Removing code signature..."
        codesign --remove-signature "$lib" 2>/dev/null

        # 5. Change ID if necessary
        if [ -n "$id_to_change" ]; then
            new_id="@loader_path/$filename"
            echo "  -> Changing ID: $id_to_change -> $new_id"
            install_name_tool -id "$new_id" "$lib"
            if [ $? -ne 0 ]; then
                echo "     Warning: Failed to change ID."
            fi
        fi

        # 6. Change dependencies if necessary
        for dep in "${deps_to_change[@]}"; do
            dep_name=$(basename "$dep")
            new_path="@loader_path/$dep_name"
            echo "  -> Changing dependency: $dep -> $new_path"
            install_name_tool -change "$dep" "$new_path" "$lib"
            if [ $? -ne 0 ]; then
                echo "     Warning: Failed to change dependency $dep."
            fi
        done

        # 7. Add @loader_path to LC_RPATH if necessary
        if [ "$has_loader_path" = false ]; then
            echo "  -> Adding @loader_path to LC_RPATH"
            install_name_tool -add_rpath "@loader_path" "$lib"
            if [ $? -ne 0 ]; then
                echo "     Warning: Failed to add @loader_path to LC_RPATH."
            fi
        fi

        # 8. Re-sign the dylib after making changes
        echo "  -> Re-signing with ad-hoc signature..."
        if command -v codesign >/dev/null 2>&1; then
            codesign -f -s - "$lib" >/dev/null 2>&1
            if [ $? -eq 0 ]; then
                echo "  -> Successfully re-signed."
            else
                echo "  -> Warning: codesign failed."
            fi
        else
            echo "  -> Warning: codesign command not found."
        fi
        echo "--------------------------------------------------"
    else
        echo "No changes needed for: $filename"
        echo "--------------------------------------------------"
    fi
done

echo "Verification and update complete."
```

注意:
- 元の dylib に LC_RPATH (`@rpath/...`) 参照がある場合、`install_name_tool -rpath` か
  `-delete_rpath` で整理。最終的に `otool -l <f> | grep -A2 LC_RPATH` を見て brew 系の
  rpath が残っていないことを確認する。
- 書き換え後は **コード署名が壊れる**（ad-hoc 署名状態になる）。配布形態によっては
  `codesign --force --sign - <f>` で ad-hoc 再署名する必要がある。社内配布や開発用なら
  そのままでも動くことが多い。

---

## 5. 検証

### 5.1 install_name に絶対パスが残っていないこと

```bash
otool -L libOpenCvSharpExtern.dylib libopencv_*.dylib \
  | grep -v "@loader_path\|^/usr/lib\|^/System\|:$\|^$" \
  | grep -v "compatibility version"
# ↑ 何も出なければ OK
# brew のパス (/opt/homebrew/... または /usr/local/...) が1行でも残っていたら NG
```

### 5.2 dlopen 単体テスト

```bash
# brew の opencv を一時的にパスから外して、bundled dylib のみで dlopen できるか
DYLD_LIBRARY_PATH="" \
  python3 -c "import ctypes; ctypes.CDLL('./libOpenCvSharpExtern.dylib'); print('OK')"
```

`OK` が出れば成功。エラーが出る場合は、足りない dylib が
メッセージに表示されるので追加コピー&書き換え。

### 5.3 .NET ビルド経由での確認

```bash
cd <repo>
dotnet publish FPVMacSideCore/FPVMacsideCore.csproj \
  -c Release -r osx-arm64 --self-contained true \
  -o bin/Release-publish/osx-arm64

ls bin/Release-publish/osx-arm64/runtimes/osx-arm64/native/
# libOpenCvSharpExtern.dylib と libopencv_*.dylib が全部並んでいれば csproj 反映 OK
```

実行後、`TimingLog.txt` に以下が出れば成功:

```
[ArUco-Debug] native-probe: FOUND   .../runtimes/osx-arm64/native/libOpenCvSharpExtern.dylib ...
[ArUco-Debug] Cv2.GetVersionString() OK, OpenCV 4.11.0
[ArUco-Debug] CvAruco.GetPredefinedDictionary(Dict4X4_50) OK.
```

---

## 6. コミット & PR

```bash
cd <repo>
git checkout -b aruco-bundle-opencv-dylibs origin/aruco-debug

# このディレクトリ配下の dylib を追加
git add Timing/native/osx-arm64/

# libOpenCvSharpExtern.dylib も install_name 書き換えで内容が変わっているので
# stage されることを確認
git status

git commit -m "ArUco: macOS arm64 用 OpenCV dylib を同梱 (@loader_path 参照に統一)"
git push origin aruco-bundle-opencv-dylibs
```

PR は `zubon2003/FPVTracksideCore` の `aruco-debug` ブランチ宛にお願いします。

csproj 側 (`Timing/Timing.csproj`) は既に `native/osx-arm64/*.dylib` を
glob で同梱する設定にする想定なので、ファイル名固定の編集は不要です
（csproj の glob 化は PR 受領前後のどちらでも対応可能）。

---

## 7. 困ったときに見るもの

- `otool -L <dylib>` — 参照先（install_name と LC_LOAD_DYLIB）
- `otool -D <dylib>` — 自分自身の LC_ID_DYLIB
- `otool -l <dylib> | grep -A2 LC_RPATH` — rpath 一覧
- 実行時の dlopen 失敗ログ: 起動コマンドに `DYLD_PRINT_LIBRARIES=1` を
  付けて起動すると、どの dylib をどの順でロードしようとして失敗したか
  tty に流れる
- `codesign -dv <dylib>` — 署名状態の確認

---

## 8. ファイルサイズ目安

参考までに、brew opencv@4.11 のサイズ感:

- `libopencv_core.411.dylib` ≈ 8 MB
- `libopencv_imgproc.411.dylib` ≈ 5 MB
- `libopencv_aruco.411.dylib` ≈ 200 KB
- `libopencv_calib3d.411.dylib` ≈ 2 MB
- `libopencv_features2d.411.dylib` ≈ 800 KB
- 全部合計でおおむね **20〜40 MB** 程度になります（dnn を含めると +60MB）。

`dnn` / `videoio` / `gapi` など ArUco に不要なモジュールは
`otool -L libOpenCvSharpExtern.dylib` に出てこなければ同梱不要なので、
最終的には 20MB 前後に収まるはずです。

-> 色々と試した結果 dylib ファイルは171個、280MBほどになりました。おそらく実際には使用していない部分が大半なのでしょうがライブラリの読み込み時に必要とされています。
