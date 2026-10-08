package signaling

var permissions = []string{"screen", "control", "admin", "file", "clipboard", "diagnostics", "runbook", "chat"}

func permissionMask(names []string) (int, bool) {
	if len(names) < 1 || len(names) > 8 {
		return 0, false
	}
	mask, last := 0, -1
	for _, name := range names {
		index := -1
		for i, known := range permissions {
			if name == known {
				index = i
				break
			}
		}
		if index <= last {
			return 0, false
		}
		last = index
		mask |= 1 << index
	}
	if mask&2 != 0 && mask&1 == 0 || mask&4 != 0 && mask&3 != 3 {
		return 0, false
	}
	return mask, true
}
