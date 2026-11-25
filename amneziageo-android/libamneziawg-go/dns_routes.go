//go:build android

package main

import (
	"encoding/binary"
	"strings"
	"sync"
	"syscall"
	"time"

	"golang.org/x/net/dns/dnsmessage"
)

type dnsQuery struct {
	client, server         uint32
	clientPort, serverPort uint16
	id                     uint16
}

type dnsQuestion struct {
	name    string
	expires int64
}

type dnsQueries struct {
	mu      sync.Mutex
	pending map[dnsQuery]dnsQuestion
}

const maxDnsQueries = 2048

// shortcut: this observer sees complete plaintext UDP/53 packets; other DNS transports need their own input.
func dnsUdp(packet []byte) (dnsQuery, []byte, bool) {
	if len(packet) < 28 || packet[0]>>4 != 4 || packet[9] != syscall.IPPROTO_UDP ||
		binary.BigEndian.Uint16(packet[6:8])&0x3fff != 0 {
		return dnsQuery{}, nil, false
	}

	head := int(packet[0]&0x0f) * 4
	if head < 20 || len(packet) < head+8 {
		return dnsQuery{}, nil, false
	}

	udp := packet[head:]
	length := int(binary.BigEndian.Uint16(udp[4:6]))
	source, dest := binary.BigEndian.Uint16(udp[0:2]), binary.BigEndian.Uint16(udp[2:4])
	if length < 20 || length > len(udp) || (source != 53 && dest != 53) {
		return dnsQuery{}, nil, false
	}

	msg := udp[8:length]
	return dnsQuery{
		binary.BigEndian.Uint32(packet[12:16]), binary.BigEndian.Uint32(packet[16:20]),
		source, dest, binary.BigEndian.Uint16(msg[:2]),
	}, msg, true
}

// Keep only actual A queries so unsolicited or unrelated answers cannot supply a route.
func (d *dnsQueries) note(packet []byte, now int64) {
	flow, data, ok := dnsUdp(packet)
	if !ok || flow.serverPort != 53 {
		return
	}

	var msg dnsmessage.Message
	if msg.Unpack(data) != nil || msg.Header.Response || len(msg.Questions) != 1 {
		return
	}

	q := msg.Questions[0]
	if q.Type != dnsmessage.TypeA || q.Class != dnsmessage.ClassINET {
		return
	}

	d.mu.Lock()
	defer d.mu.Unlock()
	if d.pending == nil {
		d.pending = make(map[dnsQuery]dnsQuestion)
	}
	if len(d.pending) >= maxDnsQueries {
		for key, value := range d.pending {
			if value.expires <= now {
				delete(d.pending, key)
			}
		}
		if len(d.pending) >= maxDnsQueries {
			return
		}
	}

	d.pending[flow] = dnsQuestion{strings.ToLower(q.Name.String()), now + int64(10*time.Second)}
}

func (d *dnsQueries) answer(packet []byte, now int64, observe func(uint32, string, uint32) int) {
	reply, data, ok := dnsUdp(packet)
	if !ok || reply.clientPort != 53 {
		return
	}

	var msg dnsmessage.Message
	if msg.Unpack(data) != nil || !msg.Header.Response || msg.Header.Truncated ||
		msg.Header.RCode != dnsmessage.RCodeSuccess || len(msg.Questions) != 1 {
		return
	}

	flow := dnsQuery{reply.server, reply.client, reply.serverPort, reply.clientPort, reply.id}
	q := msg.Questions[0]
	d.mu.Lock()
	sent, found := d.pending[flow]
	match := found && q.Type == dnsmessage.TypeA && q.Class == dnsmessage.ClassINET &&
		sent.name == strings.ToLower(q.Name.String())
	if match {
		delete(d.pending, flow)
	}
	d.mu.Unlock()
	if !match || sent.expires <= now {
		return
	}

	// Only follow the answer section from the original name. Unrelated A records have no routing meaning.
	type alias struct {
		name string
		ttl  uint32
	}
	aliases := make(map[string]alias)
	for _, record := range msg.Answers {
		if cname, ok := record.Body.(*dnsmessage.CNAMEResource); ok && record.Header.Class == dnsmessage.ClassINET {
			aliases[strings.ToLower(record.Header.Name.String())] =
				alias{strings.ToLower(cname.CNAME.String()), record.Header.TTL}
		}
	}
	reachable := map[string]uint32{}
	host, lifetime := sent.name, ^uint32(0)
	for {
		if _, seen := reachable[host]; seen {
			break
		}
		reachable[host] = lifetime
		next, ok := aliases[host]
		if !ok {
			break
		}
		host, lifetime = next.name, min(lifetime, next.ttl)
	}
	for _, record := range msg.Answers {
		a, ok := record.Body.(*dnsmessage.AResource)
		lifetime, related := reachable[strings.ToLower(record.Header.Name.String())]
		if ok && related && record.Header.Class == dnsmessage.ClassINET {
			observe(binary.BigEndian.Uint32(a.A[:]), sent.name, min(lifetime, record.Header.TTL))
		}
	}
}
