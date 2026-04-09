# FrameSaver extension — saves over Swarm websocket with a custom directory (via Swarm-side path routing).
# Mirrors SwarmSaveImageWS; must stay in sync for binary wire format.

from PIL import Image
import numpy as np
import comfy.utils
from server import PromptServer, BinaryEventTypes
import time, io, struct

SPECIAL_ID = 12345
TEXT_ID = 12347


def send_image_to_server_raw(type_num: int, save_me: callable, id: int, event_type: int = BinaryEventTypes.PREVIEW_IMAGE):
    out = io.BytesIO()
    header = struct.pack(">I", type_num)
    out.write(header)
    save_me(out)
    out.seek(0)
    preview_bytes = out.getvalue()
    server = PromptServer.instance
    server.send_sync("progress", {"value": id, "max": id}, sid=server.client_id)
    server.send_sync(event_type, preview_bytes, sid=server.client_id)


def send_text_metadata(key: str, value: str):
    full_text = f"{key}:{value}"
    send_image_to_server_raw(0, lambda out: out.write(full_text.encode("utf-8")), TEXT_ID, event_type=BinaryEventTypes.TEXT)


class FrameSaverSaveImageWithPathWS:
    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "images": ("IMAGE",),
                "path_b64": ("STRING", {"default": "", "tooltip": "UTF-8 directory path, base64-encoded (avoids ':' issues on Windows paths)."}),
            },
            "optional": {
                "bit_depth": (["8bit", "16bit", "raw"], {"default": "8bit"}),
            },
        }

    CATEGORY = "SwarmUI/images"
    RETURN_TYPES = ()
    FUNCTION = "save_images"
    OUTPUT_NODE = True
    DESCRIPTION = (
        "Like SwarmSaveImageWS, but first tells Swarm to route the next saved frame file(s) to the given directory "
        "(under the same filename pattern as normal outputs), then restores routing. Used by the FrameSaver extension."
    )

    def save_images(self, images, path_b64, bit_depth="8bit"):
        if not path_b64 or not str(path_b64).strip():
            raise RuntimeError("FrameSaverSaveImageWithPathWS: path_b64 is empty.")
        send_text_metadata("framesaver_push64", str(path_b64).strip())

        comfy.utils.ProgressBar(SPECIAL_ID)
        step = 0
        for image in images:
            if bit_depth == "raw":
                i = 255.0 * image.cpu().numpy()
                img = Image.fromarray(np.clip(i, 0, 255).astype(np.uint8))

                def do_save(out):
                    img.save(out, format="BMP")

                send_image_to_server_raw(1, do_save, SPECIAL_ID, event_type=10)
            elif bit_depth == "16bit":
                i = 65535.0 * image.cpu().numpy()
                img = self.convert_img_16bit(np.clip(i, 0, 65535).astype(np.uint16))
                send_image_to_server_raw(2, lambda out: out.write(img), SPECIAL_ID)
            else:
                i = 255.0 * image.cpu().numpy()
                img = Image.fromarray(np.clip(i, 0, 255).astype(np.uint8))

                def do_save(out):
                    img.save(out, format="PNG")

                send_image_to_server_raw(2, do_save, SPECIAL_ID)
            step += 1

        send_text_metadata("framesaver_pop", "")
        return {}

    def convert_img_16bit(self, img_np):
        try:
            import cv2

            img_np = cv2.cvtColor(img_np, cv2.COLOR_BGR2RGB)
            success, img_encoded = cv2.imencode(".png", img_np)
            if img_encoded is None or not success:
                raise RuntimeError("OpenCV failed to encode image.")
            return img_encoded.tobytes()
        except Exception as e:
            print(f"Error converting OpenCV image to PNG: {e}")
            raise

    @classmethod
    def IS_CHANGED(s, images, path_b64, bit_depth="8bit"):
        return time.time()


NODE_CLASS_MAPPINGS = {
    "FrameSaverSaveImageWithPathWS": FrameSaverSaveImageWithPathWS,
}
