export function formatDate(value: string) {
  return new Date(value).toLocaleString('vi-VN', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function shortText(value: string, length = 54) {
  return value.length > length ? `${value.slice(0, length)}...` : value;
}

export function autoSuggestionStatusText(status: string | null): string {
  switch (status) {
    case 'Running':
      return 'Đang tạo gợi ý tự động...';
    case 'Produced':
      return 'Đang áp dụng gợi ý lên ticket...';
    case 'Failed':
      return 'Tạo gợi ý thất bại, agent có thể xử lý thủ công.';
    case 'Discarded':
      return 'Gợi ý không được áp dụng vì ticket đã thay đổi hoặc đã resolve.';
    case 'Unknown':
      return 'Chưa xác định được trạng thái gợi ý tự động.';
    default:
      return 'Chưa có gợi ý sơ bộ.';
  }
}
