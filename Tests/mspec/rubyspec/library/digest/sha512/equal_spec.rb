require File.dirname(__FILE__) + '/../../../spec_helper'
require File.dirname(__FILE__) + '/shared/constants'

describe "Digest::SHA512#==" do

  it 'should be equal to itself' do
    cur_digest = Digest::SHA512.new
    cur_digest.should == cur_digest
  end

  it 'should be equal to string representing its hexdigest' do
    cur_digest = Digest::SHA512.new
    cur_digest.should == SHA512Constants::BlankHexdigest
  end

  it 'should be equal to appropriate object that responds to to_str' do
    # blank digest
    cur_digest = Digest::SHA512.new
    (obj = mock(SHA512Constants::BlankHexdigest)).should_receive(:to_str).and_return(SHA512Constants::BlankHexdigest)
    cur_digest.should == obj

    # non-blank digest
    cur_digest = Digest::SHA512.new
    cur_digest << "test"
    d_value = cur_digest.hexdigest
    (obj = mock(d_value)).should_receive(:to_str).and_return(d_value)
    cur_digest.should == obj
  end

  it 'should be equal for same digest different object' do
    cur_digest = Digest::SHA512.new
    cur_digest2 = Digest::SHA512.new
    cur_digest.should == cur_digest2
  end

end

