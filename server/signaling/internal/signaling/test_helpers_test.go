package signaling

func wrongPIN(pin string) string {
	if pin == "0000" {
		return "0001"
	}
	return "0000"
}
