# Start

sudo systemctl start coturn

# Stop

sudo systemctl stop coturn

# Status

sudo systemctl status coturn

# Restart

sudo systemctl restart coturn

# Live logs (Ctrl+C to exit)

sudo journalctl -u coturn -f

# Recent logs (not live)

sudo journalctl -u coturn --since "10 minutes ago"

# Edit config (Ctrl+O to save, Ctrl+X to exit)

sudo nano /etc/turnserver.conf

# Verbose app log file

sudo tail -f /var/log/turnserver/turnserver.log
