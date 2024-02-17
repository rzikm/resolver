docker build -t test-dns-server .
docker run --rm -it -p 1053:1053/udp test-dns-server
