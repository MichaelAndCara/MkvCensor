Only needed if you pass --word-level. Without it, muting works at the
subtitle-line level as before — this folder can stay empty.

Put two files here:
  model.onnx   (+ model.onnx.data alongside it, if the export has one —
                keep both files together, ONNX Runtime finds the .data
                file automatically as long as they're in the same folder)
  vocab.json

These need to be a wav2vec2 CTC acoustic model exported to ONNX, with its
matching character vocabulary. No Python required to USE it — this is a
one-time download.

Where to get one (no Python needed):
  https://huggingface.co/Xenova/wav2vec2-base-960h
  https://huggingface.co/ysdede/wav2vec2-base-960h-onnx
  (search "wav2vec2 onnx" on Hugging Face for other language/size options —
  bigger models are generally more accurate but slower)

Download model.onnx (and model.onnx.data if present) and vocab.json from
the model's "Files and versions" tab and drop them straight in here.

Expected model shape (this is what the app assumes):
  Input:  raw 16kHz mono float32 waveform, shape [1, num_samples]
  Output: CTC logits, shape [1, num_frames, vocab_size]
  vocab.json: {"<pad>": 0, "|": 4, "E": 5, ...} — a flat token-to-id map
              with a blank token (default expected name: "<pad>") and a
              single-character word-delimiter token (default: "|"). If
              your model's vocab uses different names for either, pass
              --alignment-blank-token / --alignment-word-delimiter.

The app zero-mean/unit-variance normalizes the audio before running the
model, matching facebook/wav2vec2-base-960h's own preprocessor config
(do_normalize: true). If you use a model where that's wrong, the results
will be noticeably worse — check the source model's preprocessor_config.json.

GPU: the app tries CUDA first (via Microsoft.ML.OnnxRuntime.Gpu) and falls
back to CPU automatically if CUDA/cuDNN aren't set up on the machine. See
https://onnxruntime.ai/docs/install/ for which CUDA/cuDNN version the
installed ONNX Runtime version needs.
