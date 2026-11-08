#!/bin/bash
# Builds the APK without Gradle: aapt2 (resources) -> javac -> d8 (dex) -> zipalign -> apksigner.
set -e
cd "$(dirname "$0")"
SDK=$HOME/android/sdk; BT=$SDK/build-tools/35.0.0; JAR=$SDK/platforms/android-34/android.jar
KS=$HOME/.config/oppo-205/android-oppo.jks
# Keystore password: $OPPO_KS_PASS, or the content of ~/.config/oppo-205/keystore.pass
export PASS=${OPPO_KS_PASS:-$(cat "$HOME/.config/oppo-205/keystore.pass" 2>/dev/null)}
[ -n "$PASS" ] || { echo "Set OPPO_KS_PASS (keystore password)"; exit 1; }
rm -rf build && mkdir -p build
# Two editions from the same sources:
#   standard -> build/oppo.apk       package fr.cinema.oppo.standard, "OPPO", no home-automation buttons
#   home     -> build/oppo-home.apk  package fr.cinema.oppo (updates the 1.0-1.2 app), "OPPO Home"
edition() {  # edition, package, label, output
  local E=build/$1
  mkdir -p $E/res $E/gen $E/classes $E/dex $E/values-src/values
  cat > $E/values-src/values/edition.xml <<XML
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <string name="app_name">$3</string>
    <string name="edition" translatable="false">$1</string>
</resources>
XML
  $BT/aapt2 compile --dir res -o $E/res/res.zip
  $BT/aapt2 compile --dir $E/values-src -o $E/res/edition.zip
  $BT/aapt2 link -I $JAR --manifest AndroidManifest.xml --rename-manifest-package $2 -o $E/base.apk --java $E/gen \
    $E/res/res.zip $E/res/edition.zip --auto-add-overlay
  javac -nowarn -g --release 8 -classpath $JAR -d $E/classes $(find src $E/gen -name '*.java') 2>&1 | grep -v "^Note:" || true
  $BT/d8 --lib $JAR --min-api 24 --release --output $E/dex $(find $E/classes -name '*.class')
  cp $E/base.apk $E/unsigned.apk
  python3 -I -c "import zipfile,sys; z=zipfile.ZipFile(sys.argv[1],'a'); z.write(sys.argv[2],'classes.dex'); z.close()" $E/unsigned.apk $E/dex/classes.dex
  $BT/zipalign -f -p 4 $E/unsigned.apk $E/aligned.apk
  $BT/apksigner sign --ks "$KS" --ks-pass env:PASS --key-pass env:PASS --out build/$4 $E/aligned.apk
  $BT/apksigner verify build/$4 && ls -la build/$4
}
if [ ! -f "$KS" ]; then
  keytool -genkeypair -keystore "$KS" -alias oppo -keyalg RSA -keysize 2048 -validity 10000 \
    -storepass "$PASS" -keypass "$PASS" -dname "CN=OPPO Emby, O=Home" >/dev/null 2>&1
  chmod 600 "$KS"
fi
edition standard fr.cinema.oppo.standard "OPPO" oppo.apk
edition home fr.cinema.oppo "OPPO Home" oppo-home.apk
