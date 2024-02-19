docker build -t test-dns-server .
docker run --rm -it -p 1053:53/udp test-dns-server
