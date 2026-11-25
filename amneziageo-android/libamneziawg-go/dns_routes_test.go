//go:build android

package main

import (
	"encoding/binary"
	"syscall"
	"testing"

	"github.com/amnezia-vpn/amneziawg-go/v3/tun"
	"github.com/bor-project/amneziageo/libamneziawg-go/owners"
	"golang.org/x/net/dns/dnsmessage"
)

func TestObservedDnsChangesDatagramDecision(t *testing.T) {
	const client, resolver = 0x0a000002, 0xc0000235
	const address = 0xc000020a
	name := dnsmessage.MustNewName("video.vpn.test.")
	alias := dnsmessage.MustNewName("edge.cdn.test.")
	question := dnsmessage.Question{Name: name, Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET}
	query := dnsmessage.Message{Header: dnsmessage.Header{ID: 42}, Questions: []dnsmessage.Question{question}}
	reply := dnsmessage.Message{
		Header:    dnsmessage.Header{ID: 42, Response: true},
		Questions: []dnsmessage.Question{question},
		Answers: []dnsmessage.Resource{
			{Header: dnsmessage.ResourceHeader{Name: name, Class: dnsmessage.ClassINET, TTL: 30}, Body: &dnsmessage.CNAMEResource{CNAME: alias}},
			{Header: dnsmessage.ResourceHeader{Name: alias, Class: dnsmessage.ClassINET, TTL: 60}, Body: &dnsmessage.AResource{A: [4]byte{192, 0, 2, 10}}},
			{Header: dnsmessage.ResourceHeader{Name: dnsmessage.MustNewName("unrelated.test."), Class: dnsmessage.ClassINET, TTL: 60}, Body: &dnsmessage.AResource{A: [4]byte{192, 0, 2, 11}}},
		},
	}

	d := &verdictTun{}
	wire := &dnsTestTun{}
	var inner tun.Device = wire
	d.inner.Store(&inner)
	observed := map[uint32]int{}
	classify := func(ip uint32, host string, ttl uint32) int {
		if host != "" {
			if host != name.String() || ttl != 30 {
				t.Fatalf("got DNS evidence %s/%d", host, ttl)
			}
			observed[ip] = 1
		}
		return observed[ip]
	}
	d.dnsHint.Store(&classify)
	owner := func(uint8, uint32, uint16, uint32, uint16) int { return owners.Block }
	d.owner.Store(&owner)
	packet := make([]byte, 28)
	packet[0], packet[9] = 0x45, syscall.IPPROTO_UDP
	binary.BigEndian.PutUint32(packet[16:20], address)
	table := parseTable("policy=split")

	response := dnsTestPacket(t, resolver, client, 53, 41000, &reply)
	if _, err := d.Write([][]byte{response}, 0); err != nil {
		t.Fatal(err)
	}
	if d.applyPolicy(table, address, packet, 0) || len(observed) != 0 {
		t.Fatal("an unsolicited response changed the app fallback")
	}

	d.policyPending.Store(true)
	d.setTable(parseTable("policy=split\ndns-pending\n192.0.2.53/32=dns-bootstrap"))
	d.setRelay(1234, true, owner)
	wire.packets = [][]byte{packet, dnsTestPacket(t, client, resolver, 41000, 53, &query)}
	buffers := [][]byte{make([]byte, 512), make([]byte, 512)}
	sizes := make([]int, len(buffers))
	if n, err := d.Read(buffers, sizes, 0); err != nil || n != 1 || binary.BigEndian.Uint32(buffers[0][16:20]) != resolver {
		t.Fatalf("bootstrap forwarded something other than the DNS question: %d, %v", n, err)
	}

	wrongResolver := dnsTestPacket(t, resolver+1, client, 53, 41000, &reply)
	if _, err := d.Write([][]byte{wrongResolver}, 0); err != nil {
		t.Fatal(err)
	}
	if len(observed) != 0 {
		t.Fatal("accepted a reply from a different resolver")
	}

	if _, err := d.Write([][]byte{response}, 0); err != nil {
		t.Fatal(err)
	}
	if len(observed) != 1 {
		t.Fatalf("expected only the address behind the requested name, got %v", observed)
	}

	d.setTable(table)
	wire.packets = [][]byte{packet}
	if n, err := d.Read(buffers, sizes, 0); err != nil || n != 1 {
		t.Fatalf("expected learned domain to override the app block: %d, %v", n, err)
	}
}

type dnsTestTun struct {
	tun.Device
	packets [][]byte
}

func (d *dnsTestTun) Read(bufs [][]byte, sizes []int, offset int) (int, error) {
	for i, packet := range d.packets {
		sizes[i] = copy(bufs[i][offset:], packet)
	}
	return len(d.packets), nil
}

func (d *dnsTestTun) Write(bufs [][]byte, offset int) (int, error) {
	return len(bufs), nil
}

func dnsTestPacket(t *testing.T, source, dest uint32, sourcePort, destPort uint16, msg *dnsmessage.Message) []byte {
	t.Helper()
	payload, err := msg.Pack()
	if err != nil {
		t.Fatal(err)
	}
	packet := make([]byte, 28+len(payload))
	packet[0], packet[9] = 0x45, syscall.IPPROTO_UDP
	binary.BigEndian.PutUint32(packet[12:16], source)
	binary.BigEndian.PutUint32(packet[16:20], dest)
	binary.BigEndian.PutUint16(packet[20:22], sourcePort)
	binary.BigEndian.PutUint16(packet[22:24], destPort)
	binary.BigEndian.PutUint16(packet[24:26], uint16(8+len(payload)))
	copy(packet[28:], payload)
	return packet
}
