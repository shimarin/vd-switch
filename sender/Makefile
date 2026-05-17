PREFIX  ?= /usr/local
DESTDIR ?=

BINDIR  = $(PREFIX)/bin
UNITDIR = /etc/systemd/system

.PHONY: install

install:
	install -Dm755 vd-switch-sender.py $(DESTDIR)$(BINDIR)/vd-switch-sender
	install -Dm644 vd-switch-sender.service $(DESTDIR)$(UNITDIR)/vd-switch-sender.service
	sed -i 's|@PREFIX@|$(PREFIX)|g' $(DESTDIR)$(UNITDIR)/vd-switch-sender.service
