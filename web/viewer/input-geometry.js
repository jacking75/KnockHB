export function videoPoint(rect, videoWidth, videoHeight, clientX, clientY) {
  if (![rect.left, rect.top, rect.width, rect.height, videoWidth, videoHeight, clientX, clientY].every(Number.isFinite)
      || rect.width <= 0 || rect.height <= 0 || videoWidth <= 0 || videoHeight <= 0) return null;
  const scale = Math.min(rect.width / videoWidth, rect.height / videoHeight);
  const width = videoWidth * scale, height = videoHeight * scale;
  const left = rect.left + (rect.width - width) / 2, top = rect.top + (rect.height - height) / 2;
  const x = (clientX - left) / width, y = (clientY - top) / height;
  return x >= 0 && x <= 1 && y >= 0 && y <= 1 ? { x, y } : null;
}
