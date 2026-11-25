//go:build android

package main

import (
	"encoding/binary"
	"syscall"
	"testing"

	"github.com/bor-project/amneziageo/libamneziawg-go/owners"
)

func TestPolicyRoutesUdpByDNSBeforeApplication(t *testing.T) {
	const address = 0xc000020a // 192.0.2.10
	for _, tt := range []struct {
		name  string
		spec  string
		owner int
		hint  int
		tun   bool
	}{
		{"known domain wins over app block", "policy=split", owners.Block, 1, true},
		{"ambiguous DNS uses app proxy", "policy=split", owners.Named, 0, true},
		{"ambiguous DNS uses app block", "policy=split", owners.Block, 0, false},
		{"explicit IP block beats domain", "policy=split\n192.0.2.10/32=block", owners.Named, 1, false},
		{"DNS block beats app proxy", "policy=full", owners.Named, 3, false},
		{"unknown owner fails closed", "policy=full", owners.Unknown, 0, false},
		{"VPN resolver stays reachable", "policy=split\n192.0.2.10/32=pinned", owners.Unknown, 0, true},
	} {
		t.Run(tt.name, func(t *testing.T) {
			packet := make([]byte, 28)
			packet[0] = 0x45
			packet[9] = syscall.IPPROTO_UDP
			binary.BigEndian.PutUint32(packet[16:20], address)
			device := &verdictTun{}
			owner := func(uint8, uint32, uint16, uint32, uint16) int { return tt.owner }
			device.owner.Store(&owner)
			hint := func(uint32, string, uint32) int { return tt.hint }
			device.dnsHint.Store(&hint)

			if got := device.applyPolicy(parseTable(tt.spec), address, packet, 0); got != tt.tun {
				t.Errorf("forward into the tunnel = %v, want %v", got, tt.tun)
			}
		})
	}
}

func TestPendingPolicyRequiresTheVerdictTable(t *testing.T) {
	device := &verdictTun{}
	device.policyPending.Store(true)

	device.setRelay(1234, true, nil)
	if !device.policyPending.Load() {
		t.Fatal("enabled forwarding before the policy was loaded")
	}

	device.setTable(parseTable("policy=split\ndns-pending\n192.0.2.10/32=dns-bootstrap"))
	device.setRelay(1234, true, nil)
	if !device.policyPending.Load() {
		t.Fatal("opened user traffic before the initial DNS table")
	}

	packet := make([]byte, 28)
	packet[0], packet[9] = 0x45, syscall.IPPROTO_UDP
	binary.BigEndian.PutUint16(packet[22:24], 53)
	if !device.bootstrapDns(device.table, 0xc000020a, packet) || device.bootstrapDns(device.table, 0xc000020b, packet) {
		t.Fatal("bootstrap DNS must go only to a configured resolver")
	}
	binary.BigEndian.PutUint16(packet[22:24], 443)
	if device.bootstrapDns(device.table, 0xc000020a, packet) {
		t.Fatal("opened user traffic to a resolver address")
	}

	device.setTable(parseTable("policy=split"))
	if device.policyPending.Load() {
		t.Fatal("forwarding is still pending after loading the policy and attaching the relay")
	}
}
