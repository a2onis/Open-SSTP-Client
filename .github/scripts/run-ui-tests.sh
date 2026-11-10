#!/usr/bin/env bash
set -o pipefail
if ! ./gradlew connectedDebugAndroidTest 2>&1 | tee "$RUNNER_TEMP/ui-test.log"; then
  python3 - <<'PY'
import glob, os, xml.etree.ElementTree as ET
messages = []
for path in glob.glob('app/build/outputs/androidTest-results/connected/**/*.xml', recursive=True):
    try:
        root = ET.parse(path).getroot()
        for node in root.iter('failure'):
            messages.append((node.text or node.get('message') or '')[:6000])
    except ET.ParseError:
        pass
if not messages:
    with open(os.path.join(os.environ['RUNNER_TEMP'], 'ui-test.log')) as f:
        messages = [''.join(f.readlines()[-60:])]
for message in messages:
    print('::error::' + message.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A'))
PY
  exit 1
fi
