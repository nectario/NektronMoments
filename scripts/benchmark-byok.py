"""Offline pipeline benchmark; generated JPEG, mock network, temporary journals.

Run with the project Python. No keys, user photos, production state or paid calls.
The baseline is trusted repository code from the previous release.
"""
from pathlib import Path
import hashlib
import json
import subprocess
import sys
import tempfile
import threading
import time
from types import ModuleType, SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from PIL import Image
from cli.nektron_moments_cli.byok import ByokRunner
from cli.nektron_moments_cli.state import LocalState
from services.enrichment.openai_scene import SceneDescriptionResult


class Backend:
    def request(self, *args, **kwargs):
        time.sleep(.005)
        return {'status': 'Succeeded' if 'description' in kwargs['json'] else 'Claimed'}


class Provider:
    def __init__(self):
        self.calls = 0
        self.lock = threading.Lock()

    def describe_bytes(self, image):
        assert image.startswith(b'\xff\xd8')
        time.sleep(.25)
        with self.lock:
            self.calls += 1
        return SceneDescriptionResult('Generated test image.', 'OpenAI', 'gpt-5.6-terra',
                                      'scene-search-v1', {'input_tokens': 100, 'output_tokens': 10})


def main():
    repo = Path(__file__).resolve().parents[1]
    baseline = ModuleType('cli.nektron_moments_cli._benchmark_baseline')
    baseline.__package__ = 'cli.nektron_moments_cli'
    code = subprocess.check_output(['git', 'show', '608eac5:cli/nektron_moments_cli/byok.py'], cwd=repo, text=True)
    exec(compile(code, 'byok-baseline-608eac5.py', 'exec'), baseline.__dict__)
    with tempfile.TemporaryDirectory(prefix='moments-byok-benchmark-') as directory:
        root = Path(directory)
        photo = root / 'generated.jpg'
        Image.new('RGB', (960, 720), '#42c4c9').save(photo)
        digest = hashlib.sha256(photo.read_bytes()).hexdigest()
        for name, runner_class, workers in [('baseline', baseline.ByokRunner, 4),
                                            ('rolling', ByokRunner, 4), ('rolling', ByokRunner, 64)]:
            provider = Provider()
            runner = runner_class(Backend(), LocalState(root / f'{name}-{workers}.sqlite3'),
                                  'test-device', workers=workers, provider=provider, progress=lambda _: None)
            for i in range(128):
                runner.journal.add('test-source', {'jobId':str(i), 'localLocator':str(photo),
                    'assetContentSha256':digest}, 'gpt-5.6-terra')
            start = time.perf_counter()
            counts = runner.run(SimpleNamespace(source_id='test-source'), 128)
            elapsed = time.perf_counter() - start
            assert counts == {'Synced':128} and provider.calls == 128
            print(json.dumps({'pipeline':name, 'workers':workers, 'images':128,
                'seconds':round(elapsed, 2), 'images_per_second':round(128/elapsed, 2),
                'paid_requests':0}), flush=True)


if __name__ == '__main__':
    main()
